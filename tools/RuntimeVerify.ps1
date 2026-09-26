[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "dist\DesktopNest.exe"
$artifactRoot = Join-Path $root "artifacts\runtime-verify"
$dataRoot = Join-Path $env:LOCALAPPDATA "DesktopNest"
$dataBackupRoot = Join-Path $artifactRoot "DesktopNest-backup"
$targetExe = Join-Path $artifactRoot "DesktopNestProbeTarget.exe"
$sourceLink = Join-Path $artifactRoot "Probe Shortcut.lnk"
$markerPath = Join-Path $artifactRoot "target-launched.txt"
$resultPath = Join-Path $artifactRoot "result.txt"

function U([int[]]$codes) {
    return -join ($codes | ForEach-Object { [char]$_ })
}

$renameMenu = U @(0x91CD, 0x547D, 0x540D)
$removeMenu = U @(0x79FB, 0x51FA, 0x6536, 0x7EB3)
$okButton = U @(0x786E, 0x5B9A)
$titleDialog = U @(0x4FEE, 0x6539, 0x5DE5, 0x5177, 0x540D, 0x79F0)
$itemDialog = U @(0x91CD, 0x547D, 0x540D, 0x6536, 0x7EB3, 0x9879)
$defaultTitle = U @(0x8F6F, 0x4EF6, 0x6536, 0x7EB3)

function Assert-Exists([string]$path, [string]$message) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$message Missing path: $path"
    }
}

function Get-MainWindowHandle([System.Diagnostics.Process]$process) {
    for ($i = 0; $i -lt 80; $i++) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "DesktopNest exited before opening its main window."
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 100
    }
    throw "DesktopNest main window was not found."
}

function Start-DesktopNest {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["DN_PROBE_MARKER"] = $markerPath
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null
    return $process
}

function Remove-ExplicitDataRoot {
    $actual = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd("\")
    $expected = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "DesktopNest")).TrimEnd("\")
    if (-not [String]::Equals(
        $actual,
        $expected,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected data path: $actual"
    }
    if (Test-Path -LiteralPath $actual) {
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}

function Find-AutomationElement([string]$name) {
    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $matches = $rootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    for ($i = 0; $i -lt $matches.Count; $i++) {
        $candidate = $matches.Item($i)
        $bounds = $candidate.Current.BoundingRectangle
        if (-not $candidate.Current.IsOffscreen -and
            $bounds.Width -gt 0 -and
            $bounds.Height -gt 0) {
            return $candidate
        }
    }
    return $null
}

function Find-AutomationMenuItem([string]$name) {
    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $matches = $rootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    for ($i = 0; $i -lt $matches.Count; $i++) {
        $candidate = $matches.Item($i)
        $bounds = $candidate.Current.BoundingRectangle
        if ($candidate.Current.ControlType -eq
            [System.Windows.Automation.ControlType]::MenuItem -and
            $candidate.Current.IsEnabled -and
            -not $candidate.Current.IsOffscreen -and
            $bounds.Width -gt 0 -and
            $bounds.Height -gt 0) {
            return $candidate
        }
    }
    return $null
}

function Invoke-AutomationElement([System.Windows.Automation.AutomationElement]$element) {
    if ($element -eq $null) {
        throw "Automation element was not found."
    }

    $bounds = $element.Current.BoundingRectangle
    if ($bounds.Width -gt 0 -and $bounds.Height -gt 0) {
        [UiProbe]::ClickAtScreen(
            [int]($bounds.Left + $bounds.Width / 2),
            [int]($bounds.Top + $bounds.Height / 2))
        return
    }

    try {
        $legacyPattern = $element.GetCurrentPattern(
            [System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
        $legacyPattern.DoDefaultAction()
        return
    }
    catch {
    }

    $invokePattern = $element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
}

function Set-DialogEditValue([string]$dialogName, [string]$value) {
    $dialog = Find-AutomationElement $dialogName
    if ($dialog -eq $null) {
        throw "Dialog was not found: $dialogName"
    }

    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $dialog.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $editCondition)
    if ($edit -eq $null) {
        throw "Dialog edit field was not found: $dialogName"
    }

    $valuePattern = $edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($value)

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $okButton)
    $button = $dialog.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $buttonCondition)
    if ($button -eq $null) {
        throw "Dialog OK button was not found."
    }
    Invoke-AutomationElement $button
}

function Save-WindowScreenshot([IntPtr]$handle, [string]$path) {
    [UiProbe]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 250
    $rect = New-Object UiProbe+RECT
    if (-not [UiProbe]::GetWindowRect($handle, [ref]$rect)) {
        throw "GetWindowRect failed."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $rect.Left,
            $rect.Top,
            0,
            0,
            [System.Drawing.Size]::new($width, $height))
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Close-WindowGracefully([IntPtr]$handle) {
    [UiProbe]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class UiProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(
        IntPtr window,
        System.Text.StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(
        IntPtr process,
        ref uint applicationUserModelIdLength,
        StringBuilder applicationUserModelId);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public static void ClickAtScreen(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static void DoubleClickAtScreen(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(100);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static void RightClickAtScreen(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }

    public static bool ClickWindowAtScreen(int x, int y)
    {
        POINT point = new POINT();
        point.X = x;
        point.Y = y;
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }
        if (!ScreenToClient(window, ref point))
        {
            return false;
        }

        int packed = (point.Y << 16) | (point.X & 0xFFFF);
        IntPtr location = new IntPtr(packed);
        if (!PostMessage(window, 0x0201, new IntPtr(1), location)) {
            return false;
        }
        System.Threading.Thread.Sleep(40);
        if (!PostMessage(window, 0x0202, IntPtr.Zero, location)) {
            return false;
        }
        return true;
    }

    public static string GetWindowClassAtScreen(int x, int y)
    {
        POINT point = new POINT();
        point.X = x;
        point.Y = y;
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return String.Empty;
        }
        System.Text.StringBuilder className =
            new System.Text.StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    public static bool SendMenuKeysAtScreen(int x, int y, uint[] keys)
    {
        POINT point = new POINT();
        point.X = x;
        point.Y = y;
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }
        for (int i = 0; i < keys.Length; i++)
        {
            SendMessage(window, 0x0100, new IntPtr(keys[i]), IntPtr.Zero);
            SendMessage(window, 0x0101, new IntPtr(keys[i]), IntPtr.Zero);
            System.Threading.Thread.Sleep(80);
        }
        return true;
    }

    public static bool SendInputClickAtScreen(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            return false;
        }
        System.Threading.Thread.Sleep(80);

        INPUT[] inputs = new INPUT[2];
        inputs[0].Type = 0;
        inputs[0].Mouse.Flags = 0x0002;
        inputs[1].Type = 0;
        inputs[1].Mouse.Flags = 0x0004;
        return SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) == 2;
    }
}
'@

if (Test-Path -LiteralPath $artifactRoot) {
    [System.IO.Directory]::Delete($artifactRoot, $true)
}
[System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null

$cscCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([String]::IsNullOrWhiteSpace($csc)) {
    throw "The Windows C# compiler was not found."
}

$targetSource = Join-Path $artifactRoot "DesktopNestProbeTarget.cs"
$targetCode = @'
using System;
using System.IO;

public static class DesktopNestProbeTarget
{
    public static void Main()
    {
        string marker = Environment.GetEnvironmentVariable("DN_PROBE_MARKER");
        if (!String.IsNullOrEmpty(marker))
        {
            File.WriteAllText(marker, DateTime.Now.ToString("o"));
        }
    }
}
'@
[System.IO.File]::WriteAllText(
    $targetSource,
    $targetCode,
    (New-Object System.Text.UTF8Encoding($false)))
& $csc /nologo /target:exe "/out:$targetExe" $targetSource
if ($LASTEXITCODE -ne 0) {
    throw "Probe target compilation failed."
}

$dragProbeExe = Join-Path $artifactRoot "DesktopNestDragProbe.exe"
$dragSource = Join-Path $artifactRoot "DesktopNestDragProbe.cs"
$dragCode = @'
using System;
using System.Windows.Forms;

public static class DesktopNestDragProbe
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Form form = new Form();
        form.Text = "DragProbe";
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(40, 60);
        form.Size = new System.Drawing.Size(140, 90);
        form.TopMost = true;
        form.MouseDown += delegate
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, new string[] { args[0] });
            form.DoDragDrop(data, DragDropEffects.Copy);
        };
        Application.Run(form);
    }
}
'@
[System.IO.File]::WriteAllText(
    $dragSource,
    $dragCode,
    (New-Object System.Text.UTF8Encoding($false)))
& $csc `
    /nologo `
    /target:winexe `
    "/out:$dragProbeExe" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $dragSource
if ($LASTEXITCODE -ne 0) {
    throw "Drag probe compilation failed."
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($sourceLink)
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $artifactRoot
$shortcut.Save()

[UiProbe]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$result = New-Object System.Collections.Generic.List[string]
$appProcess = $null
$dragProcess = $null
$hadExistingData = $false

if (Test-Path -LiteralPath $dataRoot) {
    Move-Item -LiteralPath $dataRoot -Destination $dataBackupRoot
    $hadExistingData = $true
}

try {
    Assert-Exists $exe "DesktopNest.exe was not built."
    Assert-Exists $targetExe "Probe target was not built."
    Assert-Exists $sourceLink "Probe shortcut was not created."

    $appProcess = Start-DesktopNest
    $window = Get-MainWindowHandle $appProcess
    [UiProbe]::SetWindowPos($window, [IntPtr]::Zero, 300, 100, 640, 420, 0x0044) | Out-Null
    Start-Sleep -Milliseconds 500
    $result.Add("PASS app-start")
    $result.Add("PASS no-console-window")

    $existingLinks = @()
    if (Test-Path -LiteralPath (Join-Path $dataRoot "items")) {
        $existingLinks = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot "items") -Filter *.lnk)
    }
    if ($existingLinks.Count -ne 0) {
        throw "Isolated app data was not empty."
    }

    $dragProcess = Start-Process `
        -FilePath (Join-Path $artifactRoot "DesktopNestDragProbe.exe") `
        -ArgumentList "`"$sourceLink`"" `
        -PassThru
    $dragWindow = Get-MainWindowHandle $dragProcess
    Start-Sleep -Milliseconds 400

    $dragRect = New-Object UiProbe+RECT
    [UiProbe]::GetWindowRect($dragWindow, [ref]$dragRect) | Out-Null
    $dragX = [int](($dragRect.Left + $dragRect.Right) / 2)
    $dragY = [int](($dragRect.Top + $dragRect.Bottom) / 2)
    $targetPoint = New-Object UiProbe+POINT
    $targetPoint.X = 250
    $targetPoint.Y = 300
    [UiProbe]::ClientToScreen($window, [ref]$targetPoint) | Out-Null

    [UiProbe]::SetCursorPos($dragX, $dragY) | Out-Null
    Start-Sleep -Milliseconds 150
    [UiProbe]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    for ($step = 1; $step -le 10; $step++) {
        $x = [int]($dragX + (($targetPoint.X - $dragX) * $step / 10))
        $y = [int]($dragY + (($targetPoint.Y - $dragY) * $step / 10))
        [UiProbe]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 45
    }
    [UiProbe]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1200

    $dataPath = Join-Path $dataRoot "data.json"
    Assert-Exists $dataPath "Drag/drop did not create data.json."
    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.Items.Count -ne 1) {
        throw "Drag/drop did not create exactly one item."
    }
    $storedLink = Join-Path (Join-Path $dataRoot "items") $settings.Items[0].ShortcutFileName
    Assert-Exists $storedLink "Drag/drop did not copy the shortcut."
    Assert-Exists $sourceLink "The source shortcut was removed by import."
    Save-WindowScreenshot $window (Join-Path $artifactRoot "dropped.png")
    $result.Add("PASS drag-lnk")
    $result.Add("PASS source-shortcut-preserved")

    Close-WindowGracefully $dragProcess.MainWindowHandle
    Start-Sleep -Milliseconds 200

    $clientPoint = New-Object UiProbe+POINT
    $clientPoint.X = 64
    $clientPoint.Y = 70
    [UiProbe]::ClientToScreen($window, [ref]$clientPoint) | Out-Null

    Remove-Item -LiteralPath $markerPath -ErrorAction SilentlyContinue
    [UiProbe]::SetForegroundWindow($window) | Out-Null
    [UiProbe]::DoubleClickAtScreen($clientPoint.X, $clientPoint.Y)
    Start-Sleep -Milliseconds 1200
    Assert-Exists $markerPath "Double-click did not launch the target."
    $result.Add("PASS double-click-launch")

    $markerBeforeMenu = (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc
    [UiProbe]::RightClickAtScreen($clientPoint.X, $clientPoint.Y)
    Start-Sleep -Milliseconds 500
    $renameElement = Find-AutomationMenuItem $renameMenu
    if ($renameElement -eq $null) {
        throw "Rename menu item was not found."
    }
    $renameBounds = $renameElement.Current.BoundingRectangle
    $renameX = [int]($renameBounds.Left + $renameBounds.Width / 2)
    $renameY = [int]($renameBounds.Top + $renameBounds.Height / 2)
    $hitElement = [System.Windows.Automation.AutomationElement]::FromPoint(
        (New-Object System.Windows.Point -ArgumentList $renameX, $renameY))
    if ($hitElement -ne $null) {
        Write-Output "Rename hit element: $($hitElement.Current.Name) / $($hitElement.Current.ControlType.ProgrammaticName)"
        $supportedPatterns = $hitElement.GetSupportedPatterns()
        foreach ($supportedPattern in $supportedPatterns) {
            Write-Output "Rename supported pattern: $($supportedPattern.ProgrammaticName)"
        }
        Write-Output "Rename element enabled: $($hitElement.Current.IsEnabled)"
        Write-Output "Rename hit window class: $([UiProbe]::GetWindowClassAtScreen($renameX, $renameY))"
    }
    $activated = $false
    try {
        $renameElement.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Start-Sleep -Milliseconds 250
        if (Find-AutomationElement $itemDialog) {
            $activated = $true
        }
    }
    catch {
        Write-Output "Menu focus activation failed: $($_.Exception.Message)"
    }
    if (-not $activated -and [UiProbe]::ClickWindowAtScreen($renameX, $renameY)) {
        Start-Sleep -Milliseconds 250
        if (Find-AutomationElement $itemDialog) {
            $activated = $true
        }
    }
    if (-not $activated) {
        if (-not [UiProbe]::SendInputClickAtScreen($renameX, $renameY)) {
            [UiProbe]::ClickAtScreen($renameX, $renameY)
        }
    }
    Start-Sleep -Milliseconds 350
    $markerAfterMenu = (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc
    Write-Output "Marker before/after menu click: $markerBeforeMenu / $markerAfterMenu"
    Save-WindowScreenshot $window (Join-Path $artifactRoot "after-rename-action.png")
    Set-DialogEditValue $itemDialog "RenamedItem"
    Start-Sleep -Milliseconds 500
    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.Items[0].DisplayName -ne "RenamedItem") {
        throw "Right-click rename did not persist."
    }
    $result.Add("PASS right-click-rename")

    $titlePoint = New-Object UiProbe+POINT
    $titlePoint.X = 220
    $titlePoint.Y = 16
    [UiProbe]::ClientToScreen($window, [ref]$titlePoint) | Out-Null
    [UiProbe]::SetForegroundWindow($window) | Out-Null
    [UiProbe]::DoubleClickAtScreen($titlePoint.X, $titlePoint.Y)
    Start-Sleep -Milliseconds 350
    Set-DialogEditValue $titleDialog "DesktopNest UI Check"
    Start-Sleep -Milliseconds 500
    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.Title -ne "DesktopNest UI Check") {
        throw "Title rename did not persist."
    }
    $result.Add("PASS rename-tool")

    [UiProbe]::SetWindowPos($window, [IntPtr]::Zero, 180, 140, 720, 500, 0x0044) | Out-Null
    Start-Sleep -Milliseconds 800
    [UiProbe]::ShowWindow($window, 6) | Out-Null
    Start-Sleep -Milliseconds 150
    Close-WindowGracefully $window
    if (-not $appProcess.WaitForExit(4000)) {
        throw "DesktopNest did not close gracefully."
    }
    $result.Add("PASS graceful-close")

    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.X -ne 180 -or $settings.Y -ne 140 -or $settings.Width -ne 720 -or $settings.Height -ne 500) {
        throw "Window bounds were not persisted. Actual: $($settings.X),$($settings.Y),$($settings.Width),$($settings.Height)"
    }
    $result.Add("PASS window-bounds-persisted")

    $appProcess = Start-DesktopNest
    $window = Get-MainWindowHandle $appProcess
    Start-Sleep -Milliseconds 500
    $restoredRect = New-Object UiProbe+RECT
    [UiProbe]::GetWindowRect($window, [ref]$restoredRect) | Out-Null
    $restoredWidth = $restoredRect.Right - $restoredRect.Left
    $restoredHeight = $restoredRect.Bottom - $restoredRect.Top
    if ([Math]::Abs($restoredRect.Left - 180) -gt 12 -or
        [Math]::Abs($restoredRect.Top - 140) -gt 12 -or
        [Math]::Abs($restoredWidth - 720) -gt 12 -or
        [Math]::Abs($restoredHeight - 500) -gt 12) {
        throw "Window restore mismatch: $($restoredRect.Left),$($restoredRect.Top),$restoredWidth,$restoredHeight"
    }
    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.Items.Count -ne 1 -or $settings.Title -ne "DesktopNest UI Check") {
        throw "Title or items were not restored."
    }
    Save-WindowScreenshot $window (Join-Path $artifactRoot "restored.png")
    $result.Add("PASS restore-title-position-size-items")

    [UiProbe]::ShowWindow($window, 6) | Out-Null
    Start-Sleep -Milliseconds 300
    $secondProcess = Start-DesktopNest
    if (-not $secondProcess.WaitForExit(3000)) {
        throw "Second instance did not exit."
    }
    Start-Sleep -Milliseconds 600
    if ([UiProbe]::IsIconic($window)) {
        throw "Second launch did not restore the existing window."
    }
    $result.Add("PASS single-instance-activation")

    $length = [UInt32]512
    $appId = New-Object System.Text.StringBuilder 512
    $hResult = [UiProbe]::GetApplicationUserModelId($appProcess.Handle, [ref]$length, $appId)
    if ($hResult -ne 0 -or $appId.ToString() -ne "DesktopNest.App") {
        throw "Taskbar AppUserModelID mismatch: $($appId.ToString())"
    }
    $result.Add("PASS taskbar-aumid")

    [UiProbe]::SetForegroundWindow($window) | Out-Null
    [UiProbe]::RightClickAtScreen($clientPoint.X, $clientPoint.Y)
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait("{DOWN}{ENTER}")
    Start-Sleep -Milliseconds 600
    $settings = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json
    if ($settings.Items.Count -ne 0) {
        throw "Right-click remove did not persist."
    }
    if (Test-Path -LiteralPath $storedLink) {
        throw "Stored shortcut copy was not removed."
    }
    Assert-Exists $sourceLink "Source shortcut was removed by right-click remove."
    $result.Add("PASS right-click-remove")
    $result.Add("PASS source-shortcut-still-exists")

    Close-WindowGracefully $window
    if (-not $appProcess.WaitForExit(4000)) {
        throw "DesktopNest did not close after completion."
    }
    $result.Add("PASS exe-is-windows-gui-subsystem")

    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 0x5C)
    if ($subsystem -ne 2) {
        throw "Executable subsystem is not Windows GUI: $subsystem"
    }
}
finally {
    if ($dragProcess -ne $null -and -not $dragProcess.HasExited) {
        Stop-Process -Id $dragProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($appProcess -ne $null -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        $appProcess.WaitForExit(2000) | Out-Null
    }
    if ($shell -ne $null) {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }

    Remove-ExplicitDataRoot
    if ($hadExistingData -and (Test-Path -LiteralPath $dataBackupRoot)) {
        Move-Item -LiteralPath $dataBackupRoot -Destination $dataRoot
    }
}

$result | Set-Content -LiteralPath $resultPath -Encoding UTF8
$result | ForEach-Object { Write-Output $_ }
Write-Output "Runtime verification complete: $resultPath"
