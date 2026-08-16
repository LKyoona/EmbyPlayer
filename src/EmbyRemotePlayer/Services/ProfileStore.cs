using EmbyRemotePlayer.Models;

namespace EmbyRemotePlayer.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.SettingsFile)) return MigrateLegacySession() ?? new AppSettings();

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Profiles ??= [];
            return settings;
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to read settings; starting with defaults.", ex);
            try
            {
                var backup = AppPaths.SettingsFile + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(AppPaths.SettingsFile, backup, true);
            }
            catch { }
            return MigrateLegacySession() ?? new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppPaths.EnsureDirectories();

        var tempPath = AppPaths.SettingsFile + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, AppPaths.SettingsFile, true);
    }

    public string? LoadToken(ServerProfile profile)
    {
        try
        {
            return CredentialStore.Read(profile.CredentialTarget);
        }
        catch (Exception ex)
        {
            LogService.Warning($"Unable to read token for profile {profile.ProfileId}.", ex);
            return null;
        }
    }

    public void SaveProfile(AppSettings settings, ServerProfile profile, string accessToken)
    {
        var index = settings.Profiles.FindIndex(item => item.ProfileId == profile.ProfileId);
        if (index >= 0) settings.Profiles[index] = profile;
        else settings.Profiles.Add(profile);

        settings.LastProfileId = profile.ProfileId;
        CredentialStore.Write(profile.CredentialTarget, accessToken);
        Save(settings);
    }

    public void DeleteProfile(AppSettings settings, ServerProfile profile)
    {
        settings.Profiles.RemoveAll(item => item.ProfileId == profile.ProfileId);
        if (settings.LastProfileId == profile.ProfileId)
            settings.LastProfileId = settings.Profiles.FirstOrDefault()?.ProfileId;
        Save(settings);

        try { CredentialStore.Delete(profile.CredentialTarget); }
        catch (Exception ex) { LogService.Warning($"Unable to delete credential for profile {profile.ProfileId}.", ex); }
    }

    public void ClearToken(ServerProfile profile)
    {
        try { CredentialStore.Delete(profile.CredentialTarget); }
        catch (Exception ex) { LogService.Warning($"Unable to clear credential for profile {profile.ProfileId}.", ex); }
    }

    private AppSettings? MigrateLegacySession()
    {
        var legacyPath = Path.Combine(AppPaths.DataDirectory, "session.json");
        if (!File.Exists(legacyPath)) return null;

        try
        {
            var json = File.ReadAllText(legacyPath, Encoding.UTF8);
            var legacy = JsonSerializer.Deserialize<LegacySavedSession>(json, JsonOptions);
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.ServerUrl)) return null;

            var profile = new ServerProfile
            {
                DisplayName = string.IsNullOrWhiteSpace(legacy.UserName) ? "Emby 服务器" : $"{legacy.UserName} 的 Emby",
                ServerUrl = legacy.ServerUrl,
                ServerId = legacy.ServerId,
                UserId = legacy.UserId,
                UserName = legacy.UserName,
                DeviceId = string.IsNullOrWhiteSpace(legacy.DeviceId) ? $"erp-{Guid.NewGuid():N}" : legacy.DeviceId,
                AutoConnect = legacy.RememberLogin,
                LastConnectedAt = DateTimeOffset.Now
            };
            var settings = new AppSettings { Profiles = [profile], LastProfileId = profile.ProfileId };
            var oldTarget = $"EmbyRemotePlayer:{legacy.ServerId}:{legacy.UserId}";
            var token = CredentialStore.Read(oldTarget);
            if (!string.IsNullOrWhiteSpace(token)) CredentialStore.Write(profile.CredentialTarget, token);
            Save(settings);

            try { File.Move(legacyPath, legacyPath + ".migrated-v2", true); } catch { }
            LogService.Info("Migrated the v0.1 session to a v0.2 server profile.");
            return settings;
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to migrate the legacy session.", ex);
            return null;
        }
    }

    private sealed class LegacySavedSession
    {
        public string ServerUrl { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public bool RememberLogin { get; set; }
    }
}
