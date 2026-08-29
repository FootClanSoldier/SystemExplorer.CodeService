using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SystemExplorer.CodeService;

internal static class SessionDescriptorPermissions
{
    private const UnixFileMode DescriptorDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode DescriptorFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDescriptorDirectory(string descriptorDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsDirectory(descriptorDirectory);
            return;
        }

        EnsureUnixDirectory(descriptorDirectory);
    }

    public static FileStream CreateSecureTemporaryFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsTemporaryFile(path);
        }

        return CreateUnixTemporaryFile(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsureUnixDirectory(string descriptorDirectory)
    {
        Directory.CreateDirectory(descriptorDirectory, DescriptorDirectoryMode);
        File.SetUnixFileMode(descriptorDirectory, DescriptorDirectoryMode);

        UnixFileMode actualMode = File.GetUnixFileMode(descriptorDirectory);
        if (actualMode != DescriptorDirectoryMode)
        {
            throw new UnauthorizedAccessException(
                $"descriptor directory permissions are not owner-only: {actualMode}.");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateUnixTemporaryFile(string path)
    {
        FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.SequentialScan,
                UnixCreateMode = DescriptorFileMode,
            });

        try
        {
            File.SetUnixFileMode(path, DescriptorFileMode);
            UnixFileMode actualMode = File.GetUnixFileMode(path);
            if (actualMode != DescriptorFileMode)
            {
                throw new UnauthorizedAccessException(
                    $"descriptor file permissions are not owner-only: {actualMode}.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsDirectory(string descriptorDirectory)
    {
        SecurityIdentifier currentUser = GetCurrentWindowsUserSid();

        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(
            new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

        DirectoryInfo directoryInfo = FileSystemAclExtensions.CreateDirectory(
            security,
            descriptorDirectory);

        FileSystemAclExtensions.SetAccessControl(directoryInfo, security);
        VerifyWindowsDirectory(directoryInfo, currentUser);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsTemporaryFile(string path)
    {
        SecurityIdentifier currentUser = GetCurrentWindowsUserSid();

        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(
            new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

        FileInfo fileInfo = new(path);
        FileStream stream = FileSystemAclExtensions.Create(
            fileInfo,
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.SequentialScan,
            security);

        try
        {
            FileSecurity appliedSecurity = FileSystemAclExtensions.GetAccessControl(stream);
            VerifyWindowsSecurity(appliedSecurity, currentUser, "descriptor file");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetCurrentWindowsUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new UnauthorizedAccessException(
                "the current Windows user SID could not be resolved for descriptor protection.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsDirectory(
        DirectoryInfo directoryInfo,
        SecurityIdentifier currentUser)
    {
        DirectorySecurity security = FileSystemAclExtensions.GetAccessControl(
            directoryInfo,
            AccessControlSections.Access | AccessControlSections.Owner);

        VerifyWindowsSecurity(security, currentUser, "descriptor directory");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsSecurity(
        FileSystemSecurity security,
        SecurityIdentifier currentUser,
        string targetName)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException(
                $"{targetName} ACL still inherits access rules.");
        }

        IdentityReference owner =
            security.GetOwner(typeof(SecurityIdentifier))
            ?? throw new UnauthorizedAccessException(
                $"{targetName} owner could not be resolved.");
        if (!currentUser.Equals(owner))
        {
            throw new UnauthorizedAccessException(
                $"{targetName} owner does not match the current Windows user.");
        }

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        bool hasCurrentUserFullControl = false;

        foreach (AuthorizationRule authorizationRule in rules)
        {
            if (authorizationRule is not FileSystemAccessRule accessRule)
            {
                continue;
            }

            if (!currentUser.Equals(accessRule.IdentityReference))
            {
                throw new UnauthorizedAccessException(
                    $"{targetName} grants or denies access to an unexpected Windows identity.");
            }

            if (accessRule.AccessControlType == AccessControlType.Allow
                && (accessRule.FileSystemRights & FileSystemRights.FullControl)
                    == FileSystemRights.FullControl)
            {
                hasCurrentUserFullControl = true;
            }
        }

        if (!hasCurrentUserFullControl)
        {
            throw new UnauthorizedAccessException(
                $"{targetName} does not grant the current Windows user full control.");
        }
    }
}
