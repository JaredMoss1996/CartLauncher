using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CartProjectLauncher;

internal static class Program
{
    private static readonly HashSet<string> ProcessedCartridges = new();

    private static readonly Dictionary<string, Process> RunningGames = new();

    private static readonly Dictionary<string, string> CartridgeDrives = new();

    private const uint ASFW_ANY = 0xFFFFFFFF;

    private static ManagementEventWatcher? DriveWatcher;

    [STAThread]
    private static void Main()
    {
        // Check drives that are already connected when the program starts.
        CheckExistingDrives();

        // Watch for newly inserted and removed drives.
        StartDriveWatcher();

        // Keep the launcher running in the background.
        using var waitHandle = new ManualResetEvent(false);
        waitHandle.WaitOne();
    }

    // ============================================================
    // DRIVE DETECTION
    // ============================================================

    private static void CheckExistingDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady &&
                drive.DriveType == DriveType.Removable)
            {
                CheckDrive(
                    drive.RootDirectory.FullName);
            }
        }
    }

    private static void StartDriveWatcher()
    {
        DriveWatcher = new ManagementEventWatcher(
            new WqlEventQuery(
                "SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3"));

        DriveWatcher.EventArrived += (_, e) =>
        {
            try
            {
                int eventType =
                    Convert.ToInt32(
                        e.NewEvent["EventType"]);

                string? driveName =
                    e.NewEvent["DriveName"]?.ToString();

                if (string.IsNullOrWhiteSpace(driveName))
                    return;

                driveName =
                    Path.GetPathRoot(driveName)
                    ?? driveName;

                // Drive inserted
                if (eventType == 2)
                {
                    // Give Windows time to mount the drive.
                    Thread.Sleep(500);

                    CheckDrive(driveName);
                }

                // Drive removed
                else if (eventType == 3)
                {
                    HandleDriveRemoval(driveName);
                }
            }
            catch
            {
                // Ignore individual USB detection errors.
            }
        };

        DriveWatcher.Start();
    }

    // ============================================================
    // CARTRIDGE DETECTION
    // ============================================================

    private static void CheckDrive(string driveRoot)
    {
        try
        {
            driveRoot =
                Path.GetPathRoot(driveRoot)
                ?? driveRoot;

            if (!Directory.Exists(driveRoot))
                return;

            var driveInfo =
                new DriveInfo(driveRoot);

            if (!driveInfo.IsReady)
                return;

            if (driveInfo.DriveType != DriveType.Removable)
                return;

            string iniPath =
                Path.Combine(
                    driveRoot,
                    "cartConfig.ini");

            if (!File.Exists(iniPath))
                return;

            CartInfo? cartInfo =
                ReadCartInfo(iniPath);

            if (cartInfo == null)
                return;

            if (string.IsNullOrWhiteSpace(cartInfo.Id))
                return;

            // Prevent the same cartridge from being launched
            // multiple times.
            if (ProcessedCartridges.Contains(cartInfo.Id))
                return;

            // Remember which drive this cartridge is on.
            CartridgeDrives[cartInfo.Id] =
                driveRoot;

            Process? gameProcess = null;

            // ----------------------------------------------------
            // STEAM GAME
            // ----------------------------------------------------

            if (cartInfo.Type.Equals(
                    "Steam",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(
                        cartInfo.AppId,
                        @"^\d+$"))
                {
                    CartridgeDrives.Remove(
                        cartInfo.Id);

                    return;
                }

                gameProcess =
                    LaunchSteamGame(
                        cartInfo.AppId,
                        cartInfo.LaunchArgs,
                        cartInfo.GameExe,
                        driveRoot,
                        cartInfo.X,
                        cartInfo.Y);
            }

            // ----------------------------------------------------
            // NORMAL EXE
            // ----------------------------------------------------

            else if (cartInfo.Type.Equals(
                         "Exe",
                         StringComparison.OrdinalIgnoreCase))
            {
                gameProcess =
                    LaunchExeGame(
                        cartInfo.GamePath,
                        cartInfo.LaunchArgs,
                        driveRoot,
                        cartInfo.X,
                        cartInfo.Y);
            }

            // Unknown launch type.
            else
            {
                CartridgeDrives.Remove(
                    cartInfo.Id);

                return;
            }

            // Game failed to start.
            if (gameProcess == null)
            {
                CartridgeDrives.Remove(
                    cartInfo.Id);

                return;
            }

            ProcessedCartridges.Add(
                cartInfo.Id);

            RunningGames[cartInfo.Id] =
                gameProcess;

            // Start monitoring the cartridge.
            StartCartridgeMonitor(
                cartInfo.Id,
                driveRoot,
                gameProcess);
        }
        catch
        {
            // Ignore problems with individual drives.
        }
    }

    // ============================================================
    // CARTRIDGE CONFIGURATION
    // ============================================================

    private sealed class CartInfo
    {
        public string Id { get; set; } = "";

        public string Type { get; set; } = "";

        public string AppId { get; set; } = "";

        public string GamePath { get; set; } = "";

        public string GameExe { get; set; } = "";

        public string LaunchArgs { get; set; } = "";

        /*
         * Nullable window position.
         *
         * null means the value was NOT specified in the INI.
         *
         * Positioning only happens when BOTH X and Y
         * have been specified.
         */
        public int? X { get; set; }

        public int? Y { get; set; }
    }

    private static CartInfo? ReadCartInfo(
        string iniPath)
    {
        try
        {
            var cartInfo =
                new CartInfo();

            foreach (string rawLine in File.ReadLines(iniPath))
            {
                string line =
                    rawLine.Trim();

                // Ignore comments.
                if (line.StartsWith("#") ||
                    line.StartsWith(";"))
                {
                    continue;
                }

                // Ignore section headers.
                if (line.StartsWith("["))
                    continue;

                int equalsIndex =
                    line.IndexOf('=');

                if (equalsIndex < 0)
                    continue;

                string key =
                    line[..equalsIndex].Trim();

                string value =
                    line[(equalsIndex + 1)..].Trim();

                if (key.Equals(
                        "ID",
                        StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.Id = value;
                }
                else if (key.Equals(
                             "Type",
                             StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.Type = value;
                }
                else if (key.Equals(
                             "AppID",
                             StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.AppId = value;
                }
                else if (key.Equals(
                             "GamePath",
                             StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.GamePath = value;
                }
                else if (key.Equals(
                             "GameExe",
                             StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.GameExe = value;
                }
                else if (key.Equals(
                             "LaunchArgs",
                             StringComparison.OrdinalIgnoreCase))
                {
                    cartInfo.LaunchArgs = value;
                }
                else if (key.Equals(
                             "X",
                             StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(
                            value,
                            out int x))
                    {
                        cartInfo.X = x;
                    }
                }
                else if (key.Equals(
                             "Y",
                             StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(
                            value,
                            out int y))
                    {
                        cartInfo.Y = y;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(
                    cartInfo.Id))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(
                    cartInfo.Type))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(
                    cartInfo.GameExe))
            {
                return null;
            }

            return cartInfo;
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // STEAM LAUNCHING
    // ============================================================

    private static Process? LaunchSteamGame(
        string appId,
        string launchArgs,
        string gameExe,
        string driveRoot,
        int? x,
        int? y)
    {
        try
        {
            string encodedArgs =
                Uri.EscapeDataString(
                    launchArgs);

            string steamUri;

            if (string.IsNullOrWhiteSpace(launchArgs))
            {
                steamUri =
                    $"steam://run/{appId}";
            }
            else
            {
                steamUri =
                    $"steam://run/{appId}//{encodedArgs}//";
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = steamUri,
                    UseShellExecute = true
                });

            // Wait for Steam to actually launch the game.
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);

                Process? gameProcess =
                    FindGameProcess(
                        gameExe,
                        driveRoot);

                if (gameProcess != null)
                {
                    /*
                     * The game process exists, but its window may
                     * still be initializing.
                     */
                    Task.Run(() =>
                    {
                        for (int attempt = 0;
                             attempt < 40;
                             attempt++)
                        {
                            try
                            {
                                if (gameProcess.HasExited)
                                    return;

                                gameProcess.Refresh();

                                if (gameProcess.MainWindowHandle !=
                                    IntPtr.Zero)
                                {
                                    /*
                                     * Only position the window if
                                     * BOTH X and Y were supplied.
                                     */
                                    if (x.HasValue &&
                                        y.HasValue)
                                    {
                                        PositionGameWindow(
                                            gameProcess,
                                            x.Value,
                                            y.Value);
                                    }

                                    BringGameToFront(
                                        gameProcess);
                                }
                            }
                            catch
                            {
                                // Ignore individual attempts.
                            }

                            Thread.Sleep(250);
                        }
                    });

                    return gameProcess;
                }
            }
        }
        catch
        {
            // Steam wasn't available or Windows couldn't
            // open the Steam URI.
        }

        return null;
    }

    // ============================================================
    // NORMAL EXE LAUNCHING
    // ============================================================

    private static Process? LaunchExeGame(
        string gamePath,
        string launchArgs,
        string driveRoot,
        int? x,
        int? y)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath))
                return null;

            string fullGamePath =
                Path.GetFullPath(
                    Path.Combine(
                        driveRoot,
                        gamePath));

            if (!File.Exists(fullGamePath))
                return null;

            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        fullGamePath,

                    WorkingDirectory =
                        Path.GetDirectoryName(
                            fullGamePath)
                        ?? driveRoot,

                    Arguments =
                        launchArgs,

                    UseShellExecute = true
                };

            Process? process =
                Process.Start(startInfo);

            if (process == null)
                return null;

            // Wait for the game to create its window.
            if (WaitForGameWindow(process))
            {
                /*
                 * Give Windows a little time to finish
                 * displaying the window.
                 */
                Thread.Sleep(250);

                /*
                 * Only position the window if BOTH X and Y
                 * were specified in the INI.
                 *
                 * Otherwise the game's own launch arguments
                 * control its monitor/window position.
                 */
                if (x.HasValue &&
                    y.HasValue)
                {
                    PositionGameWindow(
                        process,
                        x.Value,
                        y.Value);
                }

                /*
                 * Bring the game forward.
                 */
                BringGameToFront(
                    process);

                /*
                 * Some games reposition their window during
                 * startup, so continue enforcing the position
                 * only when positioning was explicitly enabled.
                 */
                Task.Run(() =>
                {
                    for (int attempt = 0;
                         attempt < 40;
                         attempt++)
                    {
                        try
                        {
                            if (process.HasExited)
                                return;

                            process.Refresh();

                            if (process.MainWindowHandle !=
                                IntPtr.Zero)
                            {
                                /*
                                 * Do NOT touch the window position
                                 * when X/Y aren't specified.
                                 */
                                if (x.HasValue &&
                                    y.HasValue)
                                {
                                    PositionGameWindow(
                                        process,
                                        x.Value,
                                        y.Value);
                                }

                                BringGameToFront(
                                    process);
                            }
                        }
                        catch
                        {
                            // Ignore individual attempts.
                        }

                        Thread.Sleep(250);
                    }
                });
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // POSITION GAME WINDOW
    // ============================================================

    private static void PositionGameWindow(
        Process process,
        int x,
        int y)
    {
        try
        {
            process.Refresh();

            IntPtr gameWindow =
                process.MainWindowHandle;

            if (gameWindow == IntPtr.Zero)
                return;

            /*
             * Get the current window size.
             *
             * We are only changing X and Y.
             * Width and height remain untouched.
             */
            if (!GetWindowRect(
                    gameWindow,
                    out RECT windowRect))
            {
                return;
            }

            int width =
                windowRect.Right -
                windowRect.Left;

            int height =
                windowRect.Bottom -
                windowRect.Top;

            /*
             * Move the window to the requested
             * virtual-desktop coordinates.
             */
            SetWindowPos(
                gameWindow,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                SWP_NOZORDER |
                SWP_NOACTIVATE);
        }
        catch
        {
            // Ignore window positioning errors.
        }
    }

    // ============================================================
    // WAIT FOR GAME WINDOW
    // ============================================================

    private static bool WaitForGameWindow(
        Process process)
    {
        try
        {
            for (int i = 0; i < 100; i++)
            {
                if (process.HasExited)
                    return false;

                process.Refresh();

                if (process.MainWindowHandle !=
                    IntPtr.Zero)
                {
                    return true;
                }

                Thread.Sleep(100);
            }
        }
        catch
        {
            // Ignore window detection errors.
        }

        return false;
    }

    // ============================================================
    // BRING GAME TO FRONT
    // ============================================================

    private static void BringGameToFront(
        Process process)
    {
        try
        {
            process.Refresh();

            IntPtr gameWindow =
                process.MainWindowHandle;

            if (gameWindow == IntPtr.Zero)
                return;

            // Restore the window if minimized.
            ShowWindow(
                gameWindow,
                SW_RESTORE);

            uint gameThread =
                GetWindowThreadProcessId(
                    gameWindow,
                    out _);

            uint currentThread =
                GetCurrentThreadId();

            IntPtr foregroundWindow =
                GetForegroundWindow();

            uint foregroundThread = 0;

            if (foregroundWindow != IntPtr.Zero)
            {
                foregroundThread =
                    GetWindowThreadProcessId(
                        foregroundWindow,
                        out _);
            }

            bool attachedToGame = false;
            bool attachedToForeground = false;

            try
            {
                /*
                 * Attach to the game's input thread.
                 */
                if (gameThread != currentThread)
                {
                    attachedToGame =
                        AttachThreadInput(
                            currentThread,
                            gameThread,
                            true);
                }

                /*
                 * Attach to whatever currently owns the
                 * foreground window.
                 */
                if (foregroundThread != 0 &&
                    foregroundThread != currentThread &&
                    foregroundThread != gameThread)
                {
                    attachedToForeground =
                        AttachThreadInput(
                            currentThread,
                            foregroundThread,
                            true);
                }

                /*
                 * Make the game window active.
                 */
                ShowWindow(
                    gameWindow,
                    SW_RESTORE);

                BringWindowToTop(
                    gameWindow);

                SetForegroundWindow(
                    gameWindow);

                SetActiveWindow(
                    gameWindow);

                SetFocus(
                    gameWindow);

                /*
                 * Also use AllowSetForegroundWindow.
                 */
                AllowSetForegroundWindow(
                    ASFW_ANY);

                BringWindowToTop(
                    gameWindow);

                SetForegroundWindow(
                    gameWindow);
            }
            finally
            {
                if (attachedToForeground)
                {
                    AttachThreadInput(
                        currentThread,
                        foregroundThread,
                        false);
                }

                if (attachedToGame)
                {
                    AttachThreadInput(
                        currentThread,
                        gameThread,
                        false);
                }
            }
        }
        catch
        {
            // Some games do not expose a normal Windows window.
        }
    }

    // ============================================================
    // FIND GAME PROCESS
    // ============================================================

    private static Process? FindGameProcess(
        string gameExe,
        string driveRoot)
    {
        string expectedProcessName =
            Path.GetFileNameWithoutExtension(
                gameExe);

        foreach (Process process
                 in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Equals(
                        expectedProcessName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? processPath =
                    process.MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(
                        processPath))
                {
                    continue;
                }

                /*
                 * Make sure this is the copy of the game
                 * associated with our cartridge.
                 */
                if (processPath.StartsWith(
                        driveRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                // Some Windows processes don't allow their
                // executable path to be inspected.
            }
        }

        return null;
    }

    // ============================================================
    // CARTRIDGE MONITORING
    // ============================================================

    private static void StartCartridgeMonitor(
        string cartridgeId,
        string driveRoot,
        Process gameProcess)
    {
        Task.Run(() =>
        {
            while (true)
            {
                Thread.Sleep(100);

                try
                {
                    // Game closed normally.
                    if (gameProcess.HasExited)
                    {
                        CleanupCartridge(
                            cartridgeId);

                        return;
                    }

                    // Cartridge disappeared.
                    if (!IsCartridgePresent(
                            driveRoot))
                    {
                        StopGame(
                            cartridgeId,
                            gameProcess);

                        return;
                    }
                }
                catch
                {
                    // If we can't access the cartridge,
                    // treat it as removed.
                    StopGame(
                        cartridgeId,
                        gameProcess);

                    return;
                }
            }
        });
    }

    private static bool IsCartridgePresent(
        string driveRoot)
    {
        try
        {
            if (!Directory.Exists(driveRoot))
                return false;

            var driveInfo =
                new DriveInfo(driveRoot);

            if (!driveInfo.IsReady)
                return false;

            string iniPath =
                Path.Combine(
                    driveRoot,
                    "cartConfig.ini");

            return File.Exists(iniPath);
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // CARTRIDGE REMOVAL
    // ============================================================

    private static void HandleDriveRemoval(
        string driveRoot)
    {
        string? cartridgeId = null;

        foreach (var pair
                 in CartridgeDrives)
        {
            if (pair.Value.Equals(
                    driveRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                cartridgeId =
                    pair.Key;

                break;
            }
        }

        if (cartridgeId == null)
            return;

        if (RunningGames.TryGetValue(
                cartridgeId,
                out Process? gameProcess))
        {
            StopGame(
                cartridgeId,
                gameProcess);
        }
        else
        {
            CleanupCartridge(
                cartridgeId);
        }
    }

    private static void StopGame(
        string cartridgeId,
        Process gameProcess)
    {
        try
        {
            if (!gameProcess.HasExited)
            {
                // Kill the game and its child processes.
                gameProcess.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
            // Game may have already exited.
        }
        finally
        {
            CleanupCartridge(
                cartridgeId);
        }
    }

    private static void CleanupCartridge(
        string cartridgeId)
    {
        RunningGames.Remove(
            cartridgeId);

        ProcessedCartridges.Remove(
            cartridgeId);

        CartridgeDrives.Remove(
            cartridgeId);
    }

    // ============================================================
    // WINDOWS API
    // ============================================================

    private const int SW_RESTORE = 9;

    private const uint SWP_NOZORDER = 0x0004;

    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(
        uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(
        IntPtr hWnd,
        int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        bool fAttach);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT lpRect);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);
}