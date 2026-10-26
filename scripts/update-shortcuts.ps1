[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows shortcuts can only be updated on Windows.' }
$publishedExe = (Resolve-Path -LiteralPath $Executable -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf) -or [IO.Path]::GetFileName($publishedExe) -ine 'TeXeed.exe') {
    throw 'Executable must name an existing TeXeed.exe release.'
}
$publishedDirectory = Split-Path -Parent $publishedExe
$appUserModelId = 'TeXeed.Desktop'

# Share the application identity across release paths.
if (-not ('TeXeed.Publish.ShortcutProperties' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace TeXeed.Publish
{
    public static class ShortcutProperties
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VariantArray { public uint Count; public IntPtr Data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct VariantData
        {
            [FieldOffset(0)] public IntPtr Pointer;
            [FieldOffset(0)] public VariantArray Array;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public VariantData Data;
        }
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }
        [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid id);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint change, uint flags, string path, IntPtr unused);

        private static PropertyKey AppIdKey()
        {
            return new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        }
        public static string ReadAppId(string path)
        {
            object link = new ShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                var key = AppIdKey();
                PropVariant value;
                ((IPropertyStore)link).GetValue(ref key, out value);
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Data.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        public static void WriteAppId(string path, string id)
        {
            object link = new ShellLink();
            try
            {
                var file = (IPersistFile)link;
                file.Load(path, 2);
                var key = AppIdKey();
                var value = new PropVariant { Type = 31, Data = new VariantData { Pointer = Marshal.StringToCoTaskMemUni(id) } };
                try
                {
                    var store = (IPropertyStore)link;
                    store.SetValue(ref key, ref value);
                    // Use the updated shortcut target and icon for taskbar relaunches.
                    foreach (uint propertyId in new uint[] { 2, 3, 4 })
                    {
                        var relaunchKey = AppIdKey(); relaunchKey.Id = propertyId;
                        var empty = new PropVariant(); // VT_EMPTY clears the property.
                        store.SetValue(ref relaunchKey, ref empty);
                    }
                    // Shell-link properties are persisted together by IPersistFile.
                    file.Save(path, true);
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        public static void NotifyUpdated(string path) { SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero); }
    }
}
'@
}

$shortcutShell = New-Object -ComObject WScript.Shell
try {
    function Test-TeXeedShortcut([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
        if (((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $shortcut = $null
        try {
            $shortcut = $shortcutShell.CreateShortcut($Path)
            # Match application shortcuts by target and application identity.
            if (-not [string]::IsNullOrWhiteSpace($shortcut.Arguments)) { return $false }
            if ([IO.Path]::GetFileName($shortcut.TargetPath) -ine 'TeXeed.exe') { return $false }
            $existingId = [TeXeed.Publish.ShortcutProperties]::ReadAppId($Path)
            if (-not [string]::IsNullOrWhiteSpace($existingId)) { return $existingId -ceq $appUserModelId }
            return [IO.Path]::GetFileNameWithoutExtension($Path) -match '^TeXeed(?:$|[ ._-])'
        } catch {
            Write-Warning "Cannot inspect shortcut; leaving it unchanged: $Path ($($_.Exception.Message))"
            return $false
        } finally {
            if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
    }

    function Set-TeXeedShortcut([string]$Path) {
        if ((Test-Path -LiteralPath $Path) -and -not (Test-TeXeedShortcut $Path)) {
            throw "Refusing to replace a shortcut that is not an ordinary TeXeed application shortcut: $Path"
        }
        if (-not $PSCmdlet.ShouldProcess($Path, "Set target to $publishedExe and AppUserModelID to $appUserModelId")) { return }
        $shortcutDirectory = Split-Path -Parent $Path
        [void][IO.Directory]::CreateDirectory($shortcutDirectory)
        $temporaryShortcut = Join-Path ([IO.Path]::GetTempPath()) ('TeXeed-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        $shortcut = $null
        try {
            if (Test-Path -LiteralPath $Path) { Copy-Item -LiteralPath $Path -Destination $temporaryShortcut }
            $shortcut = $shortcutShell.CreateShortcut($temporaryShortcut)
            $shortcut.TargetPath = $publishedExe
            $shortcut.WorkingDirectory = $publishedDirectory
            $shortcut.IconLocation = "$publishedExe,0"
            $shortcut.Description = 'TeXeed'
            $shortcut.Save()
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
            $shortcut = $null
            [TeXeed.Publish.ShortcutProperties]::WriteAppId($temporaryShortcut, $appUserModelId)
            Move-Item -LiteralPath $temporaryShortcut -Destination $Path -Force
            [TeXeed.Publish.ShortcutProperties]::NotifyUpdated($Path)
            Write-Host "Updated TeXeed shortcut: $Path"
        } finally {
            if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
            if (Test-Path -LiteralPath $temporaryShortcut) { Remove-Item -LiteralPath $temporaryShortcut -Force }
        }
    }

    $desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $programsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $appDataDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    if ([string]::IsNullOrWhiteSpace($desktopDirectory) -or [string]::IsNullOrWhiteSpace($programsDirectory) -or [string]::IsNullOrWhiteSpace($appDataDirectory)) {
        throw 'Current-user Desktop, Programs and ApplicationData folders must be available.'
    }
    $taskbarDirectory = Join-Path $appDataDirectory 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
    $canonicalShortcuts = @((Join-Path $desktopDirectory 'TeXeed.lnk'), (Join-Path $programsDirectory 'TeXeed.lnk'))
    $updates = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $canonicalShortcuts) { [void]$updates.Add($path) }
    $existingTaskbarShortcuts = 0
    foreach ($location in @(
        @{ Path = $desktopDirectory; Recurse = $false },
        @{ Path = $programsDirectory; Recurse = $true },
        @{ Path = $taskbarDirectory; Recurse = $false }
    )) {
        if (-not (Test-Path -LiteralPath $location.Path -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $location.Path -Filter '*.lnk' -File -Recurse:$location.Recurse) {
            if (-not (Test-TeXeedShortcut $file.FullName)) { continue }
            [void]$updates.Add($file.FullName)
            if ($location.Path -eq $taskbarDirectory) { $existingTaskbarShortcuts++ }
        }
    }
    # Validate canonical destinations before changing any shortcuts.
    foreach ($path in $canonicalShortcuts) {
        if ((Test-Path -LiteralPath $path) -and -not (Test-TeXeedShortcut $path)) {
            throw "Desktop/Start menu destination is occupied by another shortcut: $path"
        }
    }
    foreach ($path in $updates) { Set-TeXeedShortcut $path }
    if ($existingTaskbarShortcuts -eq 0) {
        Write-Host 'No existing TeXeed taskbar shortcut was found. First-time taskbar pinning requires the Windows UI.'
    } elseif ($WhatIfPreference) {
        Write-Host "Planned updates for $existingTaskbarShortcuts existing TeXeed taskbar shortcut file(s); no pinned state was changed."
    } else {
        Write-Host "Updated $existingTaskbarShortcuts existing TeXeed taskbar shortcut file(s); Windows controls the actual pinned state."
    }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell)
}
