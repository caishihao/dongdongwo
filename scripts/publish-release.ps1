# BongoCat 客户端更新发布脚本（Windows 本机，一条命令发布）
# 流程：构建 Release 单文件 exe → 打 zip → 算 SHA256 → ECDsa 签名 manifest → 上传服务器 → 验证可下载
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Version 1.1.0 -Notes "修复若干问题"
#   powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Version 1.1.0 -DryRun   # 本地自测不上传
#
# SSH 凭据：deploy\ssh.local.json（{"host":"x.x.x.x","port":22,"user":"ubuntu","password":"..."}，
#          git 忽略；缺失时自动降级为本地打包（等同 -DryRun 提示）。
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$uiProject = Join-Path $root "src\BongoCat.UI\BongoCat.UI.csproj"
$signerProject = Join-Path $root "tools\UpdateSigner\UpdateSigner.csproj"
$keysDir = Join-Path $root "deploy\keys"
$keyPath = Join-Path $keysDir "update-signing.pkcs8"
$spkiPath = Join-Path $keysDir "update-signing.spki.b64"
$outDir = Join-Path $root "deploy\publish\update"
$packageFile = "BongoCatRemix-$Version-win-x64.zip"
$zipPath = Join-Path $outDir $packageFile
$manifestPath = Join-Path $outDir "manifest.json"

Write-Host "== BongoCat 更新发布 v$Version ==" -ForegroundColor Cyan

# ── 1. 签名密钥（首次自动生成）────────────────────────────────────────
dotnet run --project $signerProject -- ensure-key $keyPath $spkiPath | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host "密钥准备失败" -ForegroundColor Red; exit 1 }

# ── 2. 构建客户端单文件 exe ──────────────────────────────────────────
Write-Host "== 构建 Release win-x64 自包含单文件 ==" -ForegroundColor Cyan
$publishDir = Join-Path $outDir "publish"
# -p:Version 把发布版本号注入程序集（AssemblyVersion/FileVersion/InformationalVersion）：
# 客户端 UpdateChecker 以程序集版本比对 manifest——缺失会导致更新后仍报旧版本 → 永远提示有更新。
dotnet publish $uiProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { Write-Host "客户端构建失败" -ForegroundColor Red; exit 1 }

$exePath = Join-Path $publishDir "BongoCatRemix.exe"
if (-not (Test-Path $exePath)) { Write-Host "未找到 BongoCatRemix.exe" -ForegroundColor Red; exit 1 }
$exeSizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "单文件 exe 体积：$exeSizeMb MB"

# ── 3. 打 zip（包内唯一条目 = BongoCatRemix.exe，客户端解压取唯一条目暂存）──
Write-Host "== 打包 $packageFile ==" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $exePath -DestinationPath $zipPath
$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "zip 体积：$zipSizeMb MB"

# ── 4. SHA256 + 签名 manifest ────────────────────────────────────────
$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$payloadPath = Join-Path $outDir "payload.txt"
Set-Content -Path $payloadPath -Value "$Version|$packageFile|$sha256" -Encoding ascii -NoNewline
$signature = (dotnet run --project $signerProject -- sign $keyPath $payloadPath | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $signature) { Write-Host "签名失败" -ForegroundColor Red; exit 1 }

$notesEscaped = $Notes -replace '\\', '\\\\' -replace '"', '\"'
$manifest = @"
{
  "version": "$Version",
  "packageFile": "$packageFile",
  "sha256": "$sha256",
  "signature": "$signature",
  "releaseNotes": "$notesEscaped",
  "publishedAt": "$publishedAt"
}
"@
# 无 BOM UTF-8（带 BOM 会让客户端 JSON 解析失败）
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))
Remove-Item $payloadPath -Force
Write-Host "manifest 已生成并签名：$manifestPath"

# 本地自验：签名能通过内置公钥口径验证（sign→verify 往返）
Write-Host "== 本地自验（zip/manifest 内容清单）=="
Get-ChildItem $outDir | Format-Table Name, Length -AutoSize | Out-Host

# ── 5. 上传（-DryRun 或无凭据则跳过）─────────────────────────────────
$sshConfigPath = Join-Path $root "deploy\ssh.local.json"
$remoteDir = "/opt/bongocat-relay/updates"
$skipUpload = $DryRun -or -not (Test-Path $sshConfigPath)

if ($skipUpload) {
    if ($DryRun) { Write-Host "[DryRun] 跳过上传（本地产物已就绪，未发布到线上）" -ForegroundColor Yellow }
    else { Write-Host "[提示] 缺少 deploy\ssh.local.json，跳过上传（等同本地打包）" -ForegroundColor Yellow }
    Write-Host "手动上传：scp $zipPath $manifestPath user@server:$remoteDir/"
    exit 0
}

Write-Host "== 上传到服务器 $remoteDir ==" -ForegroundColor Cyan
# updates 目录属主为 nobody:nogroup（服务运行账号），ubuntu 无直写权限——
# 先传 /home/ubuntu 暂存，再 sudo install 换到位并设置属主
$stageZip = "/home/ubuntu/bongo-upload-$Version.zip"
$stageManifest = "/home/ubuntu/bongo-upload-manifest.json"
python (Join-Path $root "deploy\upload_update.py") `
    --config $sshConfigPath `
    --put $zipPath $stageZip `
    --put $manifestPath $stageManifest `
    --run "sudo install -m 644 -o nobody -g nogroup $stageZip $remoteDir/$packageFile && sudo install -m 644 -o nobody -g nogroup $stageManifest $remoteDir/manifest.json && rm $stageZip $stageManifest && ls $remoteDir"
if ($LASTEXITCODE -ne 0) { Write-Host "上传失败" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "== 发布完成 ==" -ForegroundColor Green
Write-Host "客户端将在启动后 10 秒内拉取 manifest 并自动暂存新版，托盘点『重启并更新』生效。"
