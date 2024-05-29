using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.Diagnostics;
using System.Security.Policy;
using System.Windows.Threading;
using System.Reflection;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Configuration.Ini;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using static UEVR.SharedMemory;
using System.Threading.Channels;
using System.Security.Principal;

namespace UEVR {
    class GameSettingEntry : INotifyPropertyChanged {
        private string _key = "";
        private string _value = "";
        private string _tooltip = "";

        public string Key { get => _key; set => SetProperty(ref _key, value); }
        public string Value { 
            get => _value; 
            set { 
                SetProperty(ref _value, value); 
                OnPropertyChanged(nameof(ValueAsBool)); 
            } 
        }

        public string Tooltip { get => _tooltip; set => SetProperty(ref _tooltip, value); }

        public int KeyAsInt { get { return Int32.Parse(Key); } set { Key = value.ToString(); } }
        public bool ValueAsBool { 
            get => Boolean.Parse(Value);
            set { 
                Value = value.ToString().ToLower();
            } 
        }

        public Dictionary<string, string> ComboValues { get; set; } = new Dictionary<string, string>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null) {
            if (Equals(storage, value)) return false;
            if (propertyName == null) return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged(string propertyName) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    };

    enum RenderingMethod {
        [Description("原生立体")]
        NativeStereo = 0,
        [Description("同步序列")]
        SyncedSequential = 1,
        [Description("交替/AFR")]
        Alternating = 2
    };

    enum SyncedSequentialMethods {
        SkipTick = 0,
        SkipDraw = 1,
    };

    class ComboMapping {

        public static Dictionary<string, string> RenderingMethodValues = new Dictionary<string, string>(){
            {"0", "原生立体" },
            {"1", "同步序列" },
            {"2", "交替/AFR" }
        };

        public static Dictionary<string, string> SyncedSequentialMethodValues = new Dictionary<string, string>(){
            {"0", "跳过刻度" },
            {"1", "跳过绘制" },
        };

        public static Dictionary<string, Dictionary<string, string>> KeyEnums = new Dictionary<string, Dictionary<string, string>>() {
            { "VR_渲染方法", RenderingMethodValues },
            { "VR_同步序列方法", SyncedSequentialMethodValues },
        };
    };

    class MandatoryConfig {
        public static Dictionary<string, string> Entries = new Dictionary<string, string>() {
            { "VR_渲染方法", ((int)RenderingMethod.NativeStereo).ToString() },
            { "VR_同步序列方法", ((int)SyncedSequentialMethods.SkipDraw).ToString() },
            { "VR_无叠加的框架", "true" },
            { "VR_兼容性_SkipPostInitProperties", "false" }
        };
    };

    class GameSettingTooltips {
        public static string VR_RenderingMethod =
        "原生立体: 默认的，性能最好的，外观最好的渲染方法(当它工作时)。通过本机UE立体管道运行。可能会导致某些游戏的渲染错误或崩溃。\n" +
        "同步序列: AFR的一种形式。可以修复许多渲染错误。它与通常的AFR伪影完全同步。导致TAA/时间效应重影。\n" +
        "交替/AFR: AFR的最基本形式，包含所有常见的失步/伪影。一般不应该使用，除非另外两个引起问题。";

        public static string VR_SyncedSequentialMethod =
        "需要启用“同步顺序”渲染。\n" +
        "跳过刻度: 跳过下一帧的引擎刻度。通常工作正常，但有时会引起问题。\n" +
        "跳过绘制：跳过下一帧的视口绘制。使用时问题最少，但粒子效果在某些情况下可能会播放得较慢。\n";

        public static Dictionary<string, string> Entries = new Dictionary<string, string>() {
            { "VR_渲染方法", VR_RenderingMethod },
            { "VR_同步序列方法", VR_SyncedSequentialMethod },
        };
    }

    public class ValueTemplateSelector : DataTemplateSelector {
        public DataTemplate? ComboBoxTemplate { get; set; }
        public DataTemplate? TextBoxTemplate { get; set; }
        public DataTemplate? CheckboxTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) {
            var keyValuePair = (GameSettingEntry)item;
            if (ComboMapping.KeyEnums.ContainsKey(keyValuePair.Key)) {
                return ComboBoxTemplate;
            } else if (keyValuePair.Value.ToLower().Contains("true") || keyValuePair.Value.ToLower().Contains("false")) {
                return CheckboxTemplate;
            } else {
                return TextBoxTemplate;
            }
        }
    }

    public partial class MainWindow : Window {
        // variables
        // process list
        private List<Process> m_processList = new List<Process>();
        private MainWindowSettings m_mainWindowSettings = new MainWindowSettings();

        private string m_lastSelectedProcessName = new string("");
        private int m_lastSelectedProcessId = 0;

        private SharedMemory.Data? m_lastSharedData = null;
        private bool m_connected = false;

        private DispatcherTimer m_updateTimer = new DispatcherTimer {
            Interval = new TimeSpan(0, 0, 1)
        };

        private IConfiguration? m_currentConfig = null;
        private string? m_currentConfigPath = null;

        private ExecutableFilter m_executableFilter = new ExecutableFilter();
        private string? m_commandLineAttachExe = null;
        private bool m_ignoreFutureVDWarnings = false;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        private string excludedProcessesFile = "excluded.txt";

        public MainWindow() {
            InitializeComponent();

            // Grab the command-line arguments
            string[] args = Environment.GetCommandLineArgs();

            // Parse and handle arguments
            foreach (string arg in args) {
                if (arg.StartsWith("--attach=")) {
                    m_commandLineAttachExe = arg.Split('=')[1];
                }
            }
        }

        public static bool IsAdministrator() {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            if (!IsAdministrator()) {
                m_nNotificationsGroupBox.Visibility = Visibility.Visible;
                m_restartAsAdminButton.Visibility = Visibility.Visible;
                m_adminExplanation.Visibility = Visibility.Visible;
            }

            FillProcessList();
            m_openvrRadio.IsChecked = m_mainWindowSettings.OpenVRRadio;
            m_openxrRadio.IsChecked = m_mainWindowSettings.OpenXRRadio;
            m_nullifyVRPluginsCheckbox.IsChecked = m_mainWindowSettings.NullifyVRPluginsCheckbox;
            m_ignoreFutureVDWarnings = m_mainWindowSettings.IgnoreFutureVDWarnings;
            m_focusGameOnInjectionCheckbox.IsChecked = m_mainWindowSettings.FocusGameOnInjection;

            m_updateTimer.Tick += (sender, e) => Dispatcher.Invoke(MainWindow_Update);
            m_updateTimer.Start();
        }

        private static bool IsExecutableRunning(string executableName) {
            return Process.GetProcesses().Any(p => p.ProcessName.Equals(executableName, StringComparison.OrdinalIgnoreCase));
        }

        private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e) {
            // Get the path of the current executable
            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule == null) {
                return;
            }

            var exePath = mainModule.FileName;
            if (exePath == null) {
                return;
            }

            // Create a new process with administrator privileges
            var processInfo = new ProcessStartInfo {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true,
            };

            try {
                // Attempt to start the process
                Process.Start(processInfo);
            } catch (Win32Exception ex) {
                // Handle the case when the user cancels the UAC prompt or there's an error
                MessageBox.Show($"错误: {ex.Message}\n\n应用程序将在没有管理员权限的情况下继续运行。", "以管理员身份重新启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Close the current application instance
            Application.Current.Shutdown();
        }

        private DateTime m_lastAutoInjectTime = DateTime.MinValue;

        private void Update_InjectStatus() {
            if (m_connected) {
                m_injectButton.Content = "终止连接的进程";
                return;
            }

            DateTime now = DateTime.Now;
            TimeSpan oneSecond = TimeSpan.FromSeconds(1);

            if (m_commandLineAttachExe == null) {
                if (m_lastSelectedProcessId == 0) {
                    m_injectButton.Content = "注入";
                    return;
                }

                try {
                    var verifyProcess = Process.GetProcessById(m_lastSelectedProcessId);

                    if (verifyProcess == null || verifyProcess.HasExited || verifyProcess.ProcessName != m_lastSelectedProcessName) {
                        var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                        if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                            m_injectButton.Content = "等待进程";
                            return;
                        }
                    }

                    m_injectButton.Content = "注入";
                } catch (ArgumentException) {
                    var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                    if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                        m_injectButton.Content = "等待进程";
                        return;
                    }

                    m_injectButton.Content = "注入";
                }
            } else {
                m_injectButton.Content = "等待" + m_commandLineAttachExe.ToLower() + "...";

                var processes = Process.GetProcessesByName(m_commandLineAttachExe.ToLower().Replace(".exe", ""));

                if (processes.Count() == 0) {
                    return;
                }

                Process? process = null;

                foreach (Process p in processes) {
                    if (IsInjectableProcess(p)) {
                        m_lastSelectedProcessId = p.Id;
                        m_lastSelectedProcessName = p.ProcessName;
                        process = p;
                    }
                }

                if (process == null) {
                    return;
                }

                if (now - m_lastAutoInjectTime > oneSecond) {
                    if (m_nullifyVRPluginsCheckbox.IsChecked == true) {
                        IntPtr nullifierBase;
                        if (Injector.InjectDll(process.Id, "UEVRPluginNullifier.dll", out nullifierBase) && nullifierBase.ToInt64() > 0) {
                            if (!Injector.CallFunctionNoArgs(process.Id, "UEVRPluginNullifier.dll", nullifierBase, "nullify", true)) {
                                //MessageBox.Show("Failed to nullify VR plugins.");
                            }
                        } else {
                            //MessageBox.Show("Failed to inject plugin nullifier.");
                        }
                    }

                    string runtimeName;

                    if (m_openvrRadio.IsChecked == true) {
                        runtimeName = "openvr_api.dll";
                    } else if (m_openxrRadio.IsChecked == true) {
                        runtimeName = "openxr_loader.dll";
                    } else {
                        runtimeName = "openvr_api.dll";
                    }

                    if (Injector.InjectDll(process.Id, runtimeName)) {
                        InitializeConfig(process.ProcessName);

                        try {
                            if (m_currentConfig != null) {
                                if (m_currentConfig["Frontend_RequestedRuntime"] != runtimeName) {
                                    m_currentConfig["Frontend_RequestedRuntime"] = runtimeName;
                                    RefreshConfigUI();
                                    SaveCurrentConfig();
                                }
                            }
                        } catch (Exception) {

                        }

                        Injector.InjectDll(process.Id, "UEVRBackend.dll");
                    }

                    m_lastAutoInjectTime = now;
                    m_commandLineAttachExe = null; // no need anymore.
                    FillProcessList();
                    if (m_focusGameOnInjectionCheckbox.IsChecked == true)
                    {
                        SwitchToThisWindow(process.MainWindowHandle, true);
                    }
                }
            }
        }

        private void Hide_ConnectionOptions() {
            m_openGameDirectoryBtn.Visibility = Visibility.Collapsed;
        }

        private void Show_ConnectionOptions() {
            m_openGameDirectoryBtn.Visibility = Visibility.Visible;
        }

        private DateTime lastInjectorStatusUpdate = DateTime.MinValue;
        private DateTime lastFrontendSignal = DateTime.MinValue;

        private void Update_InjectorConnectionStatus() {
            var data = SharedMemory.GetData();
            DateTime now = DateTime.Now;
            TimeSpan oneSecond = TimeSpan.FromSeconds(1);

            if (data != null) {
                m_connectionStatus.Text = UEVRConnectionStatus.Connected;
                m_connectionStatus.Text += ": " + data?.path;
                m_connectionStatus.Text += "\n线程 ID: " + data?.mainThreadId.ToString();
                m_lastSharedData = data;
                m_connected = true;
                Show_ConnectionOptions();

                if (data?.signalFrontendConfigSetup == true && (now - lastFrontendSignal > oneSecond)) {
                    SharedMemory.SendCommand(SharedMemory.Command.ConfigSetupAcknowledged);
                    RefreshCurrentConfig();

                    lastFrontendSignal = now;
                }
            } else {
                if (m_connected && !string.IsNullOrEmpty(m_commandLineAttachExe))
                {
                    // If we launched with an attached game exe, we shut ourselves down once that game closes.
                    Application.Current.Shutdown();
                    return;
                }
                
                m_connectionStatus.Text = UEVRConnectionStatus.NoInstanceDetected;
                m_connected = false;
                Hide_ConnectionOptions();
            }

            lastInjectorStatusUpdate = now;
        }

        private string GetGlobalDir() {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            directory += "\\UnrealVRMod";

            if (!System.IO.Directory.Exists(directory)) {
                System.IO.Directory.CreateDirectory(directory);
            }

            return directory;
        }

        private string GetGlobalGameDir(string gameName) {
            string directory = GetGlobalDir() + "\\" + gameName;

            if (!System.IO.Directory.Exists(directory)) {
                System.IO.Directory.CreateDirectory(directory);
            }

            return directory;
        }

        private void NavigateToDirectory(string directory) {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string explorerPath = System.IO.Path.Combine(windowsDirectory, "explorer.exe");
            Process.Start(explorerPath, "\"" + directory + "\"");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton == MouseButton.Left) {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) {
            this.Close();
        }

        private string GetGlobalDirPath() {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            directory += "\\UnrealVRMod";
            return directory;
        }

        private void OpenGlobalDir_Clicked(object sender, RoutedEventArgs e) {
            string directory = GetGlobalDirPath();

            if (!System.IO.Directory.Exists(directory)) {
                System.IO.Directory.CreateDirectory(directory);
            }

            NavigateToDirectory(directory);
        }

        private void OpenGameDir_Clicked(object sender, RoutedEventArgs e) {
            if (m_lastSharedData == null) {
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(m_lastSharedData?.path);
            if (directory == null) {
                return;
            }

            NavigateToDirectory(directory);
        }
        private void ExportConfig_Clicked(object sender, RoutedEventArgs e) {
            if (!m_connected) {
                MessageBox.Show("请先注入一个游戏！");
                return;
            }

            if (m_lastSharedData == null) {
                MessageBox.Show("未检测到游戏连接。");
                return;
            }

            var dir = GetGlobalGameDir(m_lastSelectedProcessName);
            if (dir == null) {
                return;
            }

            if (!Directory.Exists(dir)) {
                MessageBox.Show("目录不存在。");
                return;
            }

            var exportedConfigsDir = GetGlobalDirPath() + "\\ExportedConfigs";

            if (!Directory.Exists(exportedConfigsDir)) {
                Directory.CreateDirectory(exportedConfigsDir);
            }

            GameConfig.CreateZipFromDirectory(dir, exportedConfigsDir + "\\" + m_lastSelectedProcessName + ".zip");
            NavigateToDirectory(exportedConfigsDir);
        }

        private void ImportConfig_Clicked(object sender, RoutedEventArgs e) {
            var importPath = GameConfig.BrowseForImport(GetGlobalDirPath());

            if (importPath == null) {
                return;
            }

            var gameName = System.IO.Path.GetFileNameWithoutExtension(importPath);
            if (gameName == null) {
                MessageBox.Show("无效的文件名");
                return;
            }

            var globalDir = GetGlobalDirPath();
            var gameGlobalDir = globalDir + "\\" + gameName;

            try {
                if (!Directory.Exists(gameGlobalDir)) {
                    Directory.CreateDirectory(gameGlobalDir);
                }

                bool wantsExtract = true;

                if (GameConfig.ZipContainsDLL(importPath)) {
                    string message = "选定的配置文件包括DLL(插件)，它可能会在您的系统上执行某些操作。\n尽量仅从受信任的来源导入带有DLL的配置以避免潜在风险。\n是否仍要继续导入？";
                    var dialog = new YesNoDialog("DLL导入提醒", message);
                    dialog.ShowDialog();

                    wantsExtract = dialog.DialogResultYes;
                }

                if (wantsExtract) {
                    var finalGameName = GameConfig.ExtractZipToDirectory(importPath, gameGlobalDir, gameName);

                    if (finalGameName == null) {
                    MessageBox.Show("无法解压缩ZIP文件。");
                        return;
                    }

                    var finalDirectory = System.IO.Path.Combine(globalDir, finalGameName);
                    NavigateToDirectory(finalDirectory);

                    RefreshCurrentConfig();


                    if (m_connected) {
                        SharedMemory.SendCommand(SharedMemory.Command.ReloadConfig);
                    }
                }
            } catch (Exception ex) {
                MessageBox.Show("发生了一个错误: " + ex.Message);
            }
        }

        private bool m_virtualDesktopWarned = false;
        private bool m_virtualDesktopChecked = false;
        private void Check_VirtualDesktop() {
            if (m_virtualDesktopWarned || m_ignoreFutureVDWarnings) {
                return;
            }

            if (IsExecutableRunning("VirtualDesktop.Streamer")) {
                m_virtualDesktopWarned = true;
                var dialog = new VDWarnDialog();
                dialog.ShowDialog();

                if (dialog.DialogResultOK) {
                    if (dialog.HideFutureWarnings) {
                        m_ignoreFutureVDWarnings = true;
                    }
                }
            }
        }

        private void MainWindow_Update() {
            Update_InjectorConnectionStatus();
            Update_InjectStatus();

            if (m_virtualDesktopChecked == false) {
                m_virtualDesktopChecked = true;
                Check_VirtualDesktop();
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            m_mainWindowSettings.OpenXRRadio = m_openxrRadio.IsChecked == true;
            m_mainWindowSettings.OpenVRRadio = m_openvrRadio.IsChecked == true;
            m_mainWindowSettings.NullifyVRPluginsCheckbox = m_nullifyVRPluginsCheckbox.IsChecked == true;
            m_mainWindowSettings.IgnoreFutureVDWarnings = m_ignoreFutureVDWarnings;
            m_mainWindowSettings.FocusGameOnInjection = m_focusGameOnInjectionCheckbox.IsChecked == true;

            m_mainWindowSettings.Save();
        }

        private string m_lastDisplayedWarningProcess = "";
        private string[] m_discouragedPlugins = {
            "OpenVR",
            "OpenXR",
            "Oculus"
        };

        private string? AreVRPluginsPresent_InEngineDir(string enginePath) {
            string pluginsPath = enginePath + "\\Binaries\\ThirdParty";

            if (!Directory.Exists(pluginsPath)) {
                return null;
            }

            foreach (string discouragedPlugin in m_discouragedPlugins) {
                string pluginPath = pluginsPath + "\\" + discouragedPlugin;

                if (Directory.Exists(pluginPath)) {
                    return pluginsPath;
                }
            }

            return null;
        }

        private string? AreVRPluginsPresent(string gameDirectory) {
            try {
                var parentPath = gameDirectory;

                for (int i = 0; i < 10; ++i) {
                    parentPath = System.IO.Path.GetDirectoryName(parentPath);

                    if (parentPath == null) {
                        return null;
                    }

                    if (Directory.Exists(parentPath + "\\Engine")) {
                        return AreVRPluginsPresent_InEngineDir(parentPath + "\\Engine");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }

            return null;
        }

        private bool IsUnrealEngineGame(string gameDirectory, string targetName) {
            try {
                if (targetName.ToLower().EndsWith("-win64-shipping")) {
                    return true;
                }

                if (targetName.ToLower().EndsWith("-wingdk-shipping")) {
                    return true;
                }

                // Check if going up the parent directories reveals the directory "\Engine\Binaries\ThirdParty".
                var parentPath = gameDirectory;
                for (int i = 0; i < 10; ++i) {  // Limit the number of directories to move up to prevent endless loops.
                    if (parentPath == null) {
                        return false;
                    }

                    if (Directory.Exists(parentPath + "\\Engine\\Binaries\\ThirdParty")) {
                        return true;
                    }

                    if (Directory.Exists(parentPath + "\\Engine\\Binaries\\Win64")) {
                        return true;
                    }

                    parentPath = System.IO.Path.GetDirectoryName(parentPath);
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }

            return false;
        }

        private string IniToString(IConfiguration config) {
            string result = "";

            foreach (var kv in config.AsEnumerable()) {
                result += kv.Key + "=" + kv.Value + "\n";
            }

            return result;
        }

        private void SaveCurrentConfig() {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var iniStr = IniToString(m_currentConfig);
                Debug.Print(iniStr);

                File.WriteAllText(m_currentConfigPath, iniStr);

                if (m_connected) {
                    SharedMemory.SendCommand(SharedMemory.Command.ReloadConfig);
                }
            } catch(Exception ex) {
                MessageBox.Show(ex.ToString());
            }
        }

        private void TextChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var textBox = (TextBox)sender;
                var keyValuePair = (GameSettingEntry)textBox.DataContext;

                // For some reason the TextBox.text is updated but thne keyValuePair.Value isn't at this point.
                bool changed = m_currentConfig[keyValuePair.Key] != textBox.Text || keyValuePair.Value != textBox.Text;
                var newValue = textBox.Text;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig[keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch(Exception ex) { 
                Console.WriteLine(ex.ToString()); 
            }
        }

        private void ComboChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var comboBox = (ComboBox)sender;
                var keyValuePair = (GameSettingEntry)comboBox.DataContext;

                bool changed = m_currentConfig[keyValuePair.Key] != keyValuePair.Value;
                var newValue = keyValuePair.Value;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig[keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }

        private void CheckChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var checkbox = (CheckBox)sender;
                var keyValuePair = (GameSettingEntry)checkbox.DataContext;

                bool changed = m_currentConfig[keyValuePair.Key] != keyValuePair.Value;
                string newValue = keyValuePair.Value;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig[keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }

        private void RefreshCurrentConfig() {
            if (m_currentConfig == null || m_currentConfigPath == null) {
                return;
            }

            InitializeConfig_FromPath(m_currentConfigPath);
        }

        private void RefreshConfigUI() {
            if (m_currentConfig == null) {
                return;
            }

            var vanillaList = m_currentConfig.AsEnumerable().ToList();
            vanillaList.Sort((a, b) => a.Key.CompareTo(b.Key));

            List<GameSettingEntry> newList = new List<GameSettingEntry>();

            foreach (var kv in vanillaList) {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) {
                    Dictionary<string, string> comboValues = new Dictionary<string, string>();
                    string tooltip = "";

                    if (ComboMapping.KeyEnums.ContainsKey(kv.Key)) {
                        var valueList = ComboMapping.KeyEnums[kv.Key];

                        if (valueList != null && valueList.ContainsKey(kv.Value)) {
                            comboValues = valueList;
                        }
                    }

                    if (GameSettingTooltips.Entries.ContainsKey(kv.Key)) {
                        tooltip = GameSettingTooltips.Entries[kv.Key];
                    }

                    newList.Add(new GameSettingEntry { Key = kv.Key, Value = kv.Value, ComboValues = comboValues, Tooltip = tooltip });
                }
            }

            if (m_iniListView.ItemsSource == null) {
                m_iniListView.ItemsSource = newList;
            } else {
                foreach (var kv in newList) {
                    var source = (List<GameSettingEntry>)m_iniListView.ItemsSource;

                    var elements = source.FindAll(el => el.Key == kv.Key);

                    if (elements.Count() == 0) {
                        // Just set the entire list, we don't care.
                        m_iniListView.ItemsSource = newList;
                        break;
                    } else {
                        elements[0].Value = kv.Value;
                        elements[0].ComboValues = kv.ComboValues;
                        elements[0].Tooltip = kv.Tooltip;
                    }
                }
            }

            m_iniListView.Visibility = Visibility.Visible;
        }

        private void InitializeConfig_FromPath(string configPath) {
            var builder = new ConfigurationBuilder().AddIniFile(configPath, optional: true, reloadOnChange: false);

            m_currentConfig = builder.Build();
            m_currentConfigPath = configPath;

            foreach (var entry in MandatoryConfig.Entries) {
                if (m_currentConfig.AsEnumerable().ToList().FindAll(v => v.Key == entry.Key).Count() == 0) {
                    m_currentConfig[entry.Key] = entry.Value;
                }
            }

            RefreshConfigUI();
        }

        private void InitializeConfig(string gameName) {
            var configDir = GetGlobalGameDir(gameName);
            var configPath = configDir + "\\config.txt";

            InitializeConfig_FromPath(configPath);
        }

        private bool m_isFirstProcessFill = true;

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            //ComboBoxItem comboBoxItem = ((sender as ComboBox).SelectedItem as ComboBoxItem);

            try {
                var box = (sender as ComboBox);
                if (box == null || box.SelectedIndex < 0 || box.SelectedIndex > m_processList.Count) {
                    return;
                }

                var p = m_processList[box.SelectedIndex];
                if (p == null || p.HasExited) {
                    return;
                }

                m_lastSelectedProcessName = p.ProcessName;
                m_lastSelectedProcessId = p.Id;

                // Search for the VR plugins inside the game directory
                // and warn the user if they exist.
                if (m_lastDisplayedWarningProcess != m_lastSelectedProcessName && p.MainModule != null) {
                    m_lastDisplayedWarningProcess = m_lastSelectedProcessName;

                    var gamePath = p.MainModule.FileName;
                    
                    if (gamePath != null) {
                        var gameDirectory = System.IO.Path.GetDirectoryName(gamePath);

                        if (gameDirectory != null) {
                            var pluginsDir = AreVRPluginsPresent(gameDirectory);

                            if (pluginsDir != null) {
                                MessageBox.Show("已在游戏安装目录中检测到VR插件。\n" +
                                                "您可能希望删除或重命名这些文件，因为它们会导致Mod出现问题。\n" +
                                                "您可能还想将-nohmd作为命令行选项传递给游戏。这有时可以在不删除任何内容的情况下工作。");
                                var result = MessageBox.Show("您想现在打开插件目录吗？", "确定", MessageBoxButton.YesNo);

                                switch (result) {
                                    case MessageBoxResult.Yes:
                                        NavigateToDirectory(pluginsDir);
                                        break;
                                    case MessageBoxResult.No:
                                        break;
                                };
                            }

                            Check_VirtualDesktop();

                            m_iniListView.ItemsSource = null; // Because we are switching processes.
                            InitializeConfig(p.ProcessName);

                            if (!IsUnrealEngineGame(gameDirectory, m_lastSelectedProcessName) && !m_isFirstProcessFill) {
                                MessageBox.Show("警告: " + m_lastSelectedProcessName + "看起来不像是虚幻引擎游戏");
                            }
                        }

                        m_lastDefaultProcessListName = GenerateProcessName(p);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }
        }

        private void ComboBox_DropDownOpened(object sender, System.EventArgs e) {
            m_lastSelectedProcessName = "";
            m_lastSelectedProcessId = 0;

            FillProcessList();
            Update_InjectStatus();

            m_isFirstProcessFill = false;
        }

        private void Donate_Clicked(object sender, RoutedEventArgs e) {
            Process.Start(new ProcessStartInfo("https://patreon.com/praydog") { UseShellExecute = true });
        }

        private void Documentation_Clicked(object sender, RoutedEventArgs e) {
            Process.Start(new ProcessStartInfo("https://praydog.github.io/uevr-docs/") { UseShellExecute = true });
        }
        private void Discord_Clicked(object sender, RoutedEventArgs e) {
            Process.Start(new ProcessStartInfo("http://flat2vr.com") { UseShellExecute = true });
        }
        private void GitHub_Clicked(object sender, RoutedEventArgs e) {
            Process.Start(new ProcessStartInfo("https://github.com/praydog/UEVR") { UseShellExecute = true });
        }

        private void Inject_Clicked(object sender, RoutedEventArgs e) {
            // "Terminate Connected Process"
            if (m_connected) {
                try {
                    var pid = m_lastSharedData?.pid;

                    if (pid != null) {
                        var target = Process.GetProcessById((int)pid);
                        target.CloseMainWindow();
                        target.Kill();
                    }
                } catch(Exception) {

                }

                return;
            }

            var selectedProcessName = m_processListBox.SelectedItem;

            if (selectedProcessName == null) {
                return;
            }

            var index = m_processListBox.SelectedIndex;
            var process = m_processList[index];

            if (process == null) {
                return;
            }

            // Double check that the process we want to inject into exists
            // this can happen if the user presses inject again while
            // the previous combo entry is still selected but the old process
            // has died.
            try {
                var verifyProcess = Process.GetProcessById(m_lastSelectedProcessId);

                if (verifyProcess == null || verifyProcess.HasExited || verifyProcess.ProcessName != m_lastSelectedProcessName) {
                    var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                    if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                        return;
                    }

                    foreach (var candidate in processes) {
                        if (IsInjectableProcess(candidate)) {
                            process = candidate;
                            break;
                        }
                    }

                    m_processList[index] = process;
                    m_processListBox.Items[index] = GenerateProcessName(process);
                    m_processListBox.SelectedIndex = index;
                }
            } catch(Exception ex) {
                MessageBox.Show(ex.Message);
                return;
            }

            string runtimeName;

            if (m_openvrRadio.IsChecked == true) {
                runtimeName = "openvr_api.dll";
            } else if (m_openxrRadio.IsChecked == true) {
                runtimeName = "openxr_loader.dll";
            } else {
                runtimeName = "openvr_api.dll";
            }

            if (m_nullifyVRPluginsCheckbox.IsChecked == true) {
                IntPtr nullifierBase;
                if (Injector.InjectDll(process.Id, "UEVRPluginNullifier.dll", out nullifierBase) && nullifierBase.ToInt64() > 0) {
                    if (!Injector.CallFunctionNoArgs(process.Id, "UEVRPluginNullifier.dll", nullifierBase, "nullify", true)) {
                        MessageBox.Show("取消使用VR插件失败。");
                    }
                } else {
                    MessageBox.Show("无法注入插件无效程序。");
                }
            }

            if (Injector.InjectDll(process.Id, runtimeName)) {
                try {
                    if (m_currentConfig != null) {
                        if (m_currentConfig["Frontend_RequestedRuntime"] != runtimeName) {
                            m_currentConfig["Frontend_RequestedRuntime"] = runtimeName;
                            RefreshConfigUI();
                            SaveCurrentConfig();
                        }
                    }
                } catch (Exception) {

                }

                Injector.InjectDll(process.Id, "UEVRBackend.dll");
            }

            if (m_focusGameOnInjectionCheckbox.IsChecked == true)
            {
                SwitchToThisWindow(process.MainWindowHandle, true);
            }
        }

        private string GenerateProcessName(Process p) {
            return p.ProcessName + " (pid: " + p.Id + ")" + " (" + p.MainWindowTitle + ")";
        }

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process([In] IntPtr hProcess, [Out] out bool wow64Process);

        private bool IsInjectableProcess(Process process) {
        
            try {
                if (Environment.Is64BitOperatingSystem) {
                    try {
                        bool isWow64 = false;
                        if (IsWow64Process(process.Handle, out isWow64) && isWow64) {
                            return false;
                        }
                    } catch {
                        // If we threw an exception here, then the process probably can't be accessed anyways.
                        return false;
                    }
                }

                if (process.MainWindowTitle.Length == 0) {
                    return false;
                }

                if (process.Id == Process.GetCurrentProcess().Id) {
                    return false;
                }

                if (!m_executableFilter.IsValidExecutable(process.ProcessName.ToLower())) {
                    return false;
                }

                foreach (ProcessModule module in process.Modules) {
                    if (module.ModuleName == null) {
                        continue;
                    }

                    string moduleLow = module.ModuleName.ToLower();
                    if (moduleLow == "d3d11.dll" || moduleLow == "d3d12.dll") {
                        return true ;
                    }
                }

                // Check if the excluded processes file exists
                if (File.Exists(excludedProcessesFile)) {
                    
                    List<string> excludedProcesses = new List<string>();
    
                    // Read excluded process names from the text file
                    excludedProcesses = File.ReadAllLines(excludedProcessesFile).ToList();
    
                    // Check if the process name is in the excluded list
                    if (excludedProcesses.Contains(process.ProcessName)) {
                        return false;
                    }
                    
                }

                return false;
            } catch(Exception ex) {
                Console.WriteLine(ex.ToString());
                return false;
            }
        }

        private bool AnyInjectableProcesses(Process[] processList) {
            foreach (Process process in processList) {
                if (IsInjectableProcess(process)) {
                    return true;
                }
            }

            return false;
        }
        private SemaphoreSlim m_processSemaphore = new SemaphoreSlim(1, 1); // create a semaphore with initial count of 1 and max count of 1
        private string? m_lastDefaultProcessListName = null;

        private async void FillProcessList() {
            // Allow the previous running FillProcessList task to finish first
            if (m_processSemaphore.CurrentCount == 0) {
                return;
            }

            await m_processSemaphore.WaitAsync();

            try {
                m_processList.Clear();
                m_processListBox.Items.Clear();

                await Task.Run(() => {
                    // get the list of processes
                    Process[] processList = Process.GetProcesses();

                    // loop through the list of processes
                    foreach (Process process in processList) {
                        if (!IsInjectableProcess(process)) {
                            continue;
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            m_processList.Add(process);
                            m_processList.Sort((a, b) => a.ProcessName.CompareTo(b.ProcessName));
                            m_processListBox.Items.Clear();

                            foreach (Process p in m_processList) {
                                string processName = GenerateProcessName(p);
                                m_processListBox.Items.Add(processName);

                                if (m_processListBox.SelectedItem == null && m_processListBox.Items.Count > 0) {
                                    if (m_lastDefaultProcessListName == null || m_lastDefaultProcessListName == processName) {
                                        m_processListBox.SelectedItem = m_processListBox.Items[m_processListBox.Items.Count - 1];
                                        m_lastDefaultProcessListName = processName;
                                    }
                                }
                            }
                        });
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        m_processListBox.Items.Clear();

                        foreach (Process process in m_processList) {
                            string processName = GenerateProcessName(process);
                            m_processListBox.Items.Add(processName);

                            if (m_processListBox.SelectedItem == null && m_processListBox.Items.Count > 0) {
                                if (m_lastDefaultProcessListName == null || m_lastDefaultProcessListName == processName) {
                                    m_processListBox.SelectedItem = m_processListBox.Items[m_processListBox.Items.Count - 1];
                                    m_lastDefaultProcessListName = processName;
                                }
                            }
                        }
                    });
                });
            } finally {
                m_processSemaphore.Release();
            }
        }

        private void TextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://www.vrzwk.com") { UseShellExecute = true });
        }
    }
}
