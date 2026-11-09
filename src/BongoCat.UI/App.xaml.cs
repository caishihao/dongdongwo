using System.IO;
using System.Windows;
using BongoCat.App;
using BongoCat.App.Economy;
using BongoCat.App.Exchange;
using BongoCat.App.Inventory;
using BongoCat.App.Pass;
using BongoCat.App.Presets;
using BongoCat.App.Styles;
using BongoCat.App.Theming;
using BongoCat.App.Tutorial;
using BongoCat.Core.Audio;
using BongoCat.Core.Config;
using BongoCat.Core.Cosmetics;
using BongoCat.Core.Input;
using BongoCat.Core.Net;
using BongoCat.Core.Save;
using BongoCat.Platform.Audio;
using BongoCat.Platform.Input;
using BongoCat.Platform.Net;
using BongoCat.Platform.Save;
using BongoCat.Platform.Update;
using BongoCat.Platform.Window;
using BongoCat.App.Update;
using BongoCat.Core.Update;
using BongoCat.Protocol;
using BongoCat.UI.Cosmetics;
using BongoCat.UI.Themes;
using BongoCat.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BongoCat.UI;

/// <summary>
/// 应用入口：Generic Host 承载 DI（architecture.md §1）。
/// 装配顺序：Platform 实现 → App 服务 → ViewModel → View，全部显式注册。
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayIconController? _tray;
    private InventoryWindow? _inventoryWindow;
    private PassWindow? _passWindow;
    private MultiplayerWindow? _multiplayerWindow;
    private ThemeManager? _themeManager;
    private TutorialController? _tutorialController;
    private RemoteCatWindowManager? _remoteCats;

    /// <summary>退出热键 ID（WndProc 分发用）。</summary>
    public const int QuitHotkeyId = 1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = BuildHost();
        _host.Start();

        var save = _host.Services.GetRequiredService<ISaveService>();
        var trayCommands = _host.Services.GetRequiredService<TrayCommandController>();
        var inventory = _host.Services.GetRequiredService<IInventoryService>();
        var economy = _host.Services.GetRequiredService<IEconomyService>();
        var pass = _host.Services.GetRequiredService<IPassService>();
        var director = _host.Services.GetRequiredService<CatDirector>();
        var styles = _host.Services.GetRequiredService<StyleCatalog>();
        var speciesCatalog = _host.Services.GetRequiredService<SpeciesCatalog>();
        var window = _host.Services.GetRequiredService<MainWindow>();

        // E2：存档提前到 OnStartup 加载（E1 在 VM.Start 里）——托盘菜单/窗口尺寸档位
        // 都依赖已加载的设置，必须先于窗口创建与托盘创建。
        save.Load();

        // E3：库存初始化（装备引用校验 + 首启动新手装授予），必须先于窗口（启动换装）与托盘
        inventory.Initialize();

        // E4：经济初始化（掉落桶过期清理 + 每日重置判定 + 每日登录奖励），在库存就绪后执行
        economy.Initialize();

        // E6：通行证初始化（赛季窗口判定 + 惰性装载 + 每日重置），在经济就绪后执行
        pass.Initialize();

        // E7：主题初始化（读存档 + 非法 id 回退默认）→ 先于任何窗口创建注入资源字典，
        //     避免启动闪色；切换事件（UI 线程触发）→ 合并字典热替换，所有已开窗口即时生效
        var theme = _host.Services.GetRequiredService<IThemeService>();
        theme.Initialize();
        _themeManager = new ThemeManager(theme);
        _themeManager.ApplyCurrent();
        theme.ThemeChanged += t => _themeManager.Apply(t);

        // E4：有效敲击 → 经济产币计数（TapStarted 已在 UI 线程触发，与 EconomyService 线程纪律一致）
        // E6：有效敲击 → 通行证 XP（订阅顺序保证晚于 economy.OnValidTap——敲击 XP 复用其计数）
        director.TapStarted += _ => economy.OnValidTap();
        director.TapStarted += _ => pass.OnValidTap();

        // E6：宝箱开启 / 合成成功 → 通行证每日任务进度（事件源转发）
        economy.ChestOpened += pass.NotifyChestOpened;
        _host.Services.GetRequiredService<IExchangeService>().ExchangeOccurred += (_, _) => pass.NotifySynthesized();

        // ── E8：云中继多人接线 ─────────────────────────────────────────────
        // 敲击上行：有效敲击 → 节流广播（30/s）；拖动上行：DragCompleted → 归一化坐标广播
        var mp = _host.Services.GetRequiredService<MultiplayerCoordinator>();
        director.TapStarted += side => mp.NotifyLocalTap(side);
        window.DragCompleted += (_, _) => mp.NotifyLocalDragCompleted();

        // E8 追加：换装 → 装扮广播（500ms 防抖合并；InventoryService.Changed 覆盖装备/库存变化，
        // 通知内部读存档当前装备值，与背包 UI 展示同源）
        var mpInventory = _host.Services.GetRequiredService<IInventoryService>();
        mpInventory.Changed += mp.NotifyOutfitChanged;

        // 远程猫窗口管理：成员上/下线建窗/渐隐，位置/敲击/风格/装扮驱动（事件在客户端后台线程触发，
        // 一律 Dispatcher 跳 UI 线程——WPF 视觉树与窗口只允许创建线程触碰）
        _remoteCats = new RemoteCatWindowManager(
            _host.Services.GetRequiredService<ITransparentWindowService>(), styles, FileLog.Write);
        var audio = _host.Services.GetRequiredService<IAudioService>();
        mp.RemoteMemberJoined += (id, name) => Dispatcher.BeginInvoke(() =>
        {
            _remoteCats.OnMemberJoined(id, name);
            // 建窗即套用已缓存的物种/装扮与风格（先来后到：对方同步早于我建窗时缓存已就位）
            if (mp.TryGetRemoteSpecies(id) is { } speciesByte)
            {
                _remoteCats.OnSpecies(id, speciesByte);
            }

            if (mp.TryGetRemoteCosmetics(id) is { } cosmetics)
            {
                _remoteCats.OnCosmetics(id, cosmetics.Hat, cosmetics.Skin, cosmetics.Bongos);
            }

            if (mp.TryGetRemoteStyle(id) is { } styleByte)
            {
                _remoteCats.OnStyle(id, styleByte);
            }
        });
        mp.RemoteMemberLeft += id => Dispatcher.BeginInvoke(() => _remoteCats.OnMemberLeft(id));
        mp.RemoteCatStateReceived += (id, x, y) => Dispatcher.BeginInvoke(() => _remoteCats.OnCatState(id, x, y));
        mp.RemoteStyleReceived += (id, styleByte) =>
            _ = Dispatcher.BeginInvoke(() => _remoteCats.OnStyle(id, styleByte));
        mp.RemoteSpeciesReceived += (id, speciesByte) =>
            _ = Dispatcher.BeginInvoke(() => _remoteCats.OnSpecies(id, speciesByte));
        mp.CosmeticSyncReceived += (id, hat, skin, bongos) =>
            _ = Dispatcher.BeginInvoke(() => _remoteCats.OnCosmetics(id, hat, skin, bongos));
        mp.RemoteTapReceived += (id, side) => Dispatcher.BeginInvoke(() =>
        {
            _remoteCats.OnTap(id, side);
            // 远端敲击音效（06-multiplayer.md §4：远端猫音量固定低音量——V1 跟随主音量，
            // 独立衰减音轨列为 V3+；音效组与侧别一致保持左右手节奏感）
            audio.Play(side == TapSide.Left ? AudioGroups.DrumLow : AudioGroups.DrumHigh);
        });
        mp.StateChanged += state =>
        {
            if (state is MpConnectionState.Disconnected or MpConnectionState.Failed or MpConnectionState.Reconnecting)
            {
                // 断线表现：远程猫窗口渐隐消失（重连成功后成员上/下线事件会重建）
                _ = Dispatcher.BeginInvoke(() => _remoteCats.ClearAll(state.ToString()));
            }
        };

        // ── E7：新手教程接线（非模态：敲击/开背包通知随时到达，服务内部按当前步守卫，
        //        后台经济/掉落照常跑——GDD-07 §7）────────────────────────────
        var tutorial = _host.Services.GetRequiredService<ITutorialService>();
        director.TapStarted += _ => tutorial.NotifyTap();                       // 步 0「敲敲看」计数
        window.DragCompleted += (dx, dy) => tutorial.NotifyDragEnded(dx, dy);   // 步 1「拖拖看」位移
        trayCommands.InventoryRequested += tutorial.NotifyInventoryOpened;      // 步 2「找背包」（含气泡直达按钮）

        // E4/E6：经济 + 通行证心跳 ~1Hz（滴灌/宝箱充能/掉落 roll/存档节流；赛季惰性切换/每日重置）
        var economyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        economyTimer.Tick += (_, _) =>
        {
            economy.Tick();
            pass.Tick();
        };
        economyTimer.Start();

        trayCommands.ApplyStartupSettings(); // 音量/静音同步进音频服务（E2 前只存档不生效）

        // 退出强制保存（ADR-004 规则 3：退出时强制保存）
        Exit += (_, _) =>
        {
            FileLog.Write("[App] Exit：强制保存存档");
            save.Save();
        };

        // 烟测钩子：--smoke-exit-ms=N → 启动 N 毫秒后自动优雅退出（CI/烟测专用，正常使用不传）
        string? smokeArg = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-exit-ms=", StringComparison.Ordinal));
        if (smokeArg is not null && int.TryParse(smokeArg["--smoke-exit-ms=".Length..], out int exitMs))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(exitMs) };
            timer.Tick += (_, _) =>
            {
                FileLog.Write("[App] 烟测定时退出触发");
                Shutdown();
            };
            timer.Start();
        }

        window.Show();

        // ── GDD-08/GDD-09：风格与物种系统接线 ─────────────────────────────
        // 托盘"形象▸风格"/首启选择器 → SetCatStyle（写存档 + 广播）→ 主猫重载模板 + 多人上行重发 CAT_STATE
        trayCommands.StyleChanged += styleId =>
        {
            window.ApplyStyle(styleId);
            mp.NotifyStyleChanged();
        };

        // 托盘"形象▸物种"/首启选择器页签 → SetCatSpecies（写存档 + 广播）→
        // ① 物种限定装备清洗（dino_default 等跨物种装备卸下，GDD-09 §1）→
        // ② 主猫重载形体模板（风格保持）→ ③ 多人上行重发 CAT_STATE（携带 species 字节）
        trayCommands.SpeciesChanged += speciesId =>
        {
            inventory.ValidateSpeciesCompatibility();
            window.ApplySpecies(speciesId);
            mp.NotifyStyleChanged();
        };

        // GDD-08 §3 / GDD-09 §3：首启"第 0 步：选你的伙伴"（教程前置；触发条件 = TutorialCompleted==false
        // 且 StyleChosen==false，矩阵见 StylePickerPolicy——选过风格没做完教程不重复弹）。
        // 确认/跳过后 SetCatSpecies + SetCatStyle 写入存档（StyleChosen=true），窗口 OnLoaded 会读到所选形象。
        if (StylePickerPolicy.ShouldShow(save.Current.TutorialCompleted, save.Current.StyleChosen))
        {
            var picker = new StylePickerWindow(styles, speciesCatalog);
            picker.ShowDialog();
            string chosenSpecies = picker.SelectedSpeciesId ?? SpeciesCatalog.DefaultSpeciesId;
            string chosenStyle = picker.SelectedStyleId ?? StyleCatalog.DefaultStyleId;
            trayCommands.SetCatSpecies(chosenSpecies); // 跳过 = (cat, clay)（GDD-09 §3.1）
            trayCommands.SetCatStyle(chosenStyle);
            FileLog.Write($"[Species] 首启形象选择完成：species={chosenSpecies} style={chosenStyle}");
        }

        // E2：托盘图标（NotifyIcon 需在 UI 线程创建；菜单命令经 TrayCommandController 路由）。
        // E7：传入主题服务 → 托盘"主题"子菜单三选一（勾选态单一数据源 = 存档 UiThemeId）
        // GDD-08/GDD-09：传入风格+物种目录 → 托盘"形象"下物种/风格两个并列子菜单（勾选态单一数据源 = 存档）
        _tray = new TrayIconController(trayCommands, window, FileLog.Write,
            _host.Services.GetRequiredService<IThemeService>(), styles, speciesCatalog);

        // E3：托盘"背包" → 打开/激活库存窗口（单实例缓存；关闭即释放，重开重建）
        trayCommands.InventoryRequested += ShowInventoryWindow;

        // E6：托盘"通行证" → 打开/激活通行证窗口（与背包同模式单实例缓存）
        trayCommands.PassRequested += ShowPassWindow;

        // E8：托盘"多人" → 打开/激活多人联机窗口（同模式单实例缓存）
        trayCommands.MultiplayerRequested += ShowMultiplayerWindow;

        // E7：托盘"新手教程" → 重播（完成态也能重看；重播不动经济/装扮/赛季字段）
        _tutorialController = new TutorialController(
            tutorial,
            window.GetCatAnchorScreenPoint,
            () => trayCommands.OpenInventory(),
            FileLog.Write);
        trayCommands.TutorialReplayRequested += () => _tutorialController.Replay();

        // E7：首次启动（TutorialCompleted 未置位）触发分步教程；老玩家 v6→v7 迁移后也会看一次（引导新功能）
        // GDD-08：选择器（若弹出）已在此前完成——教程第 1 步看到的就是"自己的猫"
        if (!save.Current.TutorialCompleted)
        {
            _tutorialController.StartFromSave();
        }

        // E4：掉落 → 托盘气泡提示（事件已在 UI 线程触发：Tick/OnValidTap 均经 Dispatcher）
        economy.DropOccurred += drop =>
        {
            string text = drop.Kind switch
            {
                DropKind.Item when drop.WasDuplicate && drop.Item is not null
                    => $"捡到 {drop.Item.DisplayName}（重复 → +{drop.DuplicateCoins} 金币）",
                DropKind.Item when drop.Item is not null
                    => $"捡到新装扮：{drop.Item.DisplayName}！",
                DropKind.CoinPack => $"捡到金币包：+{drop.Coins} 金币",
                _ => string.Empty, // Nothing/Suppressed 不弹气泡（Suppressed 已在背包头部提示）
            };

            if (text.Length > 0)
            {
                _tray?.ShowDropBalloon("BongoCat 掉落", text);
                FileLog.Write($"[Economy] 掉落气泡：{text}");
            }
        };

        // E6：通行证里程碑可领取 → 托盘气泡（复用掉落气泡模式，GDD-05 里程碑气泡原创实现）
        pass.MilestoneReady += level =>
        {
            _tray?.ShowDropBalloon("BongoCat 通行证", $"通行证 Lv{level} 里程碑奖励可领取！");
            FileLog.Write($"[Pass] 里程碑气泡：Lv{level} 可领取");
        };

        // E6：赛季切换 → 一次性公告气泡（切换为次日 0 点惰性触发，不打断会话，GDD-05 §7）
        pass.SeasonSwitched += seasonName =>
        {
            _tray?.ShowDropBalloon("BongoCat 通行证", $"新赛季开启：{seasonName}");
            FileLog.Write($"[Pass] 赛季切换公告：{seasonName}");
        };

        // ── 自更新接线（启动 10s 后台检查，不阻塞启动；暂存就绪 → 托盘菜单"重启并更新"）──
        WireSelfUpdate();
    }

    /// <summary>自更新全链路：编排器 + 托盘气泡 + 手动检查 + 重启并更新（换位流程见 SelfUpdateApplier）。</summary>
    private void WireSelfUpdate()
    {
        var orchestrator = new UpdateOrchestrator(
            new HttpUpdateClient(UpdatePaths.ResolveUpdateBaseUrl()),
            UpdatePaths.ResolveUpdateDir(),
            log: FileLog.Write);

        // 程序级退出请求：若有暂存更新 → 启动暂存 exe --apply-update 再退出（换位由新进程接管）
        _host!.Services.GetRequiredService<TrayCommandController>().RestartUpdateRequested += () =>
        {
            string? stagedExe = orchestrator.StagedExePath;
            if (stagedExe is null || !File.Exists(stagedExe))
            {
                FileLog.Write("[Update] 重启并更新被拒：暂存 exe 不存在");
                return;
            }

            string? currentExe = Environment.ProcessPath;
            if (currentExe is null)
            {
                return;
            }

            FileLog.Write($"[Update] 用户确认重启并更新：暂存 {stagedExe} → 换位 {currentExe}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = stagedExe,
                Arguments = $"--apply-update={currentExe}",
                UseShellExecute = true,
            });
            _host!.Services.GetRequiredService<TrayCommandController>().Exit();
        };

        // 后台检查完成 → 状态回填托盘 + 气泡提示（事件在后台线程，气泡必须跳 UI 线程）
        orchestrator.UpdateStaged += manifest =>
        {
            _host!.Services.GetRequiredService<TrayCommandController>().HasPendingUpdate = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                _tray?.ShowUpdateBalloon("BongoCat 更新",
                    $"新版本 v{manifest.Version} 已就绪，托盘菜单点\"重启并更新\"生效" +
                    (manifest.ReleaseNotes.Length > 0 ? $"\n{manifest.ReleaseNotes}" : string.Empty));
            });
        };
        orchestrator.UpdateFailed += reason => Dispatcher.BeginInvoke(() =>
            _tray?.ShowUpdateBalloon("BongoCat 更新失败", reason, warning: true));

        // 托盘"检查更新"（手动）：异步执行，完成态气泡反馈
        _host!.Services.GetRequiredService<TrayCommandController>().CheckUpdateRequested += () =>
        {
            FileLog.Write("[Update] 手动检查更新触发");
            _ = Task.Run(async () =>
            {
                bool ok = await orchestrator.CheckNowAsync();
                if (ok && orchestrator.State == UpdateState.NoUpdate)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                        _tray?.ShowUpdateBalloon("BongoCat 更新", "已是最新版本"));
                }
            });
        };

        // 启动 10s 后台检查（不阻塞启动）；上次会话遗留的暂存直接恢复 Staged 态
        if (orchestrator.DetectExistingStaged())
        {
            _host!.Services.GetRequiredService<TrayCommandController>().HasPendingUpdate = true;
        }
        else
        {
            var updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Stop();
                _ = Task.Run(() => orchestrator.CheckNowAsync());
            };
            updateTimer.Start();
        }
    }

    private void ShowInventoryWindow()
    {
        if (_inventoryWindow is null)
        {
            _inventoryWindow = new InventoryWindow(
                _host!.Services.GetRequiredService<InventoryViewModel>(),
                () => _host!.Services.GetRequiredService<TrayCommandController>().OpenPass());
            _inventoryWindow.Closed += (_, _) => _inventoryWindow = null;
        }

        _inventoryWindow.Show();
        _inventoryWindow.Activate();
        FileLog.Write("[App] 背包窗口已打开");
    }

    private void ShowPassWindow()
    {
        if (_passWindow is null)
        {
            _passWindow = new PassWindow(_host!.Services.GetRequiredService<PassViewModel>());
            _passWindow.Closed += (_, _) => _passWindow = null;
        }

        _passWindow.Show();
        _passWindow.Activate();
        FileLog.Write("[App] 通行证窗口已打开");
    }

    private void ShowMultiplayerWindow()
    {
        if (_multiplayerWindow is null)
        {
            _multiplayerWindow = new MultiplayerWindow(
                _host!.Services.GetRequiredService<MultiplayerCoordinator>(),
                FileLog.Write);
            _multiplayerWindow.Closed += (_, _) => _multiplayerWindow = null;
        }

        _multiplayerWindow.Show();
        _multiplayerWindow.Activate();
        FileLog.Write("[App] 多人联机窗口已打开");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }

    /// <summary>DI 装配（教学要点：所有跨层服务只依赖接口，实现在此一处换）。</summary>
    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // 基础设施
        builder.Services.AddSingleton<GameConfig>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(new Random(20251107)); // 种子可复现（Core 编码规则）

        // Platform 实现（换引擎/出 Mac 版 = 只换这一段，architecture.md §1 理由 2）
        builder.Services.AddSingleton<IKeyHookService>(sp =>
            new KeyPollingService(sp.GetRequiredService<GameConfig>(), FileLog.Write));
        builder.Services.AddSingleton<IAudioService>(sp =>
            new NAudioAudioService(sp.GetRequiredService<Random>(), FileLog.Write));
        builder.Services.AddSingleton<ISaveService>(_ => new JsonSaveService(FileLog.Write));
        builder.Services.AddSingleton<ITransparentWindowService, TransparentWindowService>();

        // E3：物品目录（items.json 嵌入资源）+ 库存服务
        // E5：库存上限折算价取 economy.json（inventory.maxPerItem + salvage 表）；
        //     折算金币经延迟解析的 sink 回调走经济层（避免 Inventory↔Economy 构造期循环依赖）
        builder.Services.AddSingleton<CosmeticCatalog>(_ => ItemCatalogLoader.LoadFromAssembly(FileLog.Write));
        builder.Services.AddSingleton<IInventoryService>(sp =>
            new InventoryService(
                sp.GetRequiredService<ISaveService>(),
                sp.GetRequiredService<CosmeticCatalog>(),
                sp.GetRequiredService<EconomyConfig>(),
                (itemId, coins) => sp.GetRequiredService<IEconomyService>().GrantCoins(
                    coins, CoinSource.OverflowConversion),
                FileLog.Write));

        // E4：经济数值（economy.json 嵌入资源）+ 经济服务
        builder.Services.AddSingleton<EconomyConfig>(_ => EconomyConfigLoader.LoadFromAssembly(FileLog.Write));
        builder.Services.AddSingleton<IEconomyService>(sp =>
            new EconomyService(
                sp.GetRequiredService<ISaveService>(),
                sp.GetRequiredService<IInventoryService>(),
                sp.GetRequiredService<EconomyConfig>(),
                sp.GetRequiredService<Random>(),
                sp.GetRequiredService<TimeProvider>(),
                FileLog.Write));

        // E5：兑换合成（同品质 3 合 1 + 手动拆解）+ 装扮预设
        builder.Services.AddSingleton<IExchangeService>(sp =>
            new ExchangeService(
                sp.GetRequiredService<IInventoryService>(),
                sp.GetRequiredService<IEconomyService>(),
                sp.GetRequiredService<EconomyConfig>(),
                sp.GetRequiredService<Random>(),
                FileLog.Write));
        builder.Services.AddSingleton<IPresetService>(sp =>
            new PresetService(
                sp.GetRequiredService<ISaveService>(),
                sp.GetRequiredService<IInventoryService>(),
                FileLog.Write));

        // E6：赛季配置（seasons/*.json 嵌入资源，随版本热更）+ 通行证服务
        builder.Services.AddSingleton<IReadOnlyList<SeasonConfig>>(sp =>
            SeasonConfigLoader.LoadFromAssembly(sp.GetRequiredService<CosmeticCatalog>(), FileLog.Write));
        builder.Services.AddSingleton<IPassService>(sp =>
            new PassService(
                sp.GetRequiredService<ISaveService>(),
                sp.GetRequiredService<IReadOnlyList<SeasonConfig>>(),
                sp.GetRequiredService<IInventoryService>(),
                sp.GetRequiredService<IEconomyService>(),
                sp.GetRequiredService<TimeProvider>(),
                FileLog.Write));

        // E7：新手教程（三步纯逻辑）+ UI 主题（切换/持久化/非法 id 防御）
        builder.Services.AddSingleton<ITutorialService>(sp =>
            new TutorialService(sp.GetRequiredService<ISaveService>(), FileLog.Write));
        builder.Services.AddSingleton<IThemeService>(sp =>
            new ThemeService(sp.GetRequiredService<ISaveService>(), FileLog.Write));

        // GDD-08：风格目录（styles.json 嵌入资源，fail-fast）——渲染/选择器/托盘/多人共同数据源
        builder.Services.AddSingleton<StyleCatalog>(_ => StyleCatalogLoader.LoadFromAssembly(FileLog.Write));

        // GDD-09：物种目录（species.json 嵌入资源，fail-fast）——渲染/选择器/托盘/多人共同数据源
        builder.Services.AddSingleton<SpeciesCatalog>(_ => SpeciesCatalogLoader.LoadFromAssembly(FileLog.Write));

        // GDD-09 §7：sprite 物种资产目录（anchors.json + 位图嵌入资源，防御式）
        //   ——注入 StyleRenderer 静态入口；缺资产/解析失败时 sprite 物种回退 vector cat
        StyleRenderer.SpriteSpecies = SpriteSpeciesCatalog.LoadFromAssembly(FileLog.Write);

        // E8：多人客户端（Platform TCP 实现）+ 编排协调器（敲击/位置/风格/装扮/成员事件中转）
        builder.Services.AddSingleton<IMultiplayerClient>(_ =>
            new MultiplayerClientService(FileLog.Write));
        builder.Services.AddSingleton<MultiplayerCoordinator>(sp =>
            new MultiplayerCoordinator(
                sp.GetRequiredService<IMultiplayerClient>(),
                sp.GetRequiredService<ISaveService>(),
                sp.GetRequiredService<TimeProvider>(),
                FileLog.Write,
                sp.GetRequiredService<CosmeticCatalog>(),
                sp.GetRequiredService<StyleCatalog>(),
                sp.GetRequiredService<SpeciesCatalog>()));

        // App 层编排
        builder.Services.AddSingleton<CatDirector>();
        builder.Services.AddSingleton<TrayCommandController>();

        // UI 层
        builder.Services.AddSingleton<CatViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<InventoryViewModel>();
        builder.Services.AddSingleton<PassViewModel>();

        return builder.Build();
    }
}
