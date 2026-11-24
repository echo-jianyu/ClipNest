using System.IO;
using Microsoft.Win32;

namespace ClipNest.Services;

public sealed class AutoStartService
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipNest";

    private readonly RegistryKey _registryRoot;
    private readonly string _runKeyPath;
    private readonly string _valueName;
    private readonly string _executablePath;

    public AutoStartService(string executablePath)
        : this(
            executablePath,
            Registry.CurrentUser,
            RunKeyPath,
            ValueName)
    {
    }

    internal AutoStartService(
        string executablePath,
        RegistryKey registryRoot,
        string runKeyPath,
        string valueName)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _registryRoot = registryRoot ?? throw new ArgumentNullException(nameof(registryRoot));
        _runKeyPath = runKeyPath ?? throw new ArgumentNullException(nameof(runKeyPath));
        _valueName = valueName ?? throw new ArgumentNullException(nameof(valueName));
    }

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        try
        {
            if (enabled)
            {
                Enable();
            }
            else
            {
                Disable();
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void Enable()
    {
        using var key = _registryRoot.CreateSubKey(_runKeyPath);
        if (key is null)
        {
            throw new IOException("无法打开开机启动项注册表位置。");
        }

        key.SetValue(
            _valueName,
            $"\"{_executablePath}\"",
            RegistryValueKind.String);
    }

    private void Disable()
    {
        using var key = _registryRoot.OpenSubKey(_runKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
