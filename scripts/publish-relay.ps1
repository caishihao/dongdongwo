# BongoCat E8 云中继发布脚本（Windows 本机运行）
# 产出：deploy/publish/ 下的 linux-x64 自包含单文件 bongocat-relay
# 用法：powershell -ExecutionPolicy Bypass -File scripts\publish-relay.ps1 [-Configuration Release]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\BongoCat.RelayServer\BongoCat.RelayServer.csproj"
$outDir = Join-Path $root "deploy\publish"

Write-Host "== 发布 BongoCat.RelayServer ($Configuration / linux-x64 自包含单文件) ==" -ForegroundColor Cyan

dotnet publish $project `
    -c $Configuration `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败（dotnet publish 退出码 $LASTEXITCODE）" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "== 发布完成 ==" -ForegroundColor Green
Write-Host "产物目录：$outDir"
Write-Host "产物：bongocat-relay（Linux 可执行，无需目标机安装 .NET）"
Write-Host ""
Write-Host "下一步（详见 deploy\DEPLOY.md）："
Write-Host "  1. 上传 bongocat-relay 到腾讯云服务器（scp deploy\publish\bongocat-relay user@server:/opt/bongocat-relay/）"
Write-Host "  2. 上传 deploy\bongocat-relay.service 到 /etc/systemd/system/"
Write-Host "  3. systemctl daemon-reload && systemctl enable --now bongocat-relay"
Write-Host "  4. 腾讯云控制台安全组放行 TCP 9527"
Write-Host "  5. 客户端托盘 - 多人 - 填服务器 IP 验证"
