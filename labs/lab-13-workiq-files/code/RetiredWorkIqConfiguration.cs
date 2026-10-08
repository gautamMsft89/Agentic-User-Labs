namespace WorkIqFiles;

// Effective compatibility markers are always inactive in the native-only host.
internal sealed class SetupMode { public bool Enabled { get; set; } }
internal sealed class ChannelSetup { public bool Enabled { get; set; } }

internal static class RetiredWorkIqConfiguration
{
    internal static string Apply(IConfiguration configuration, FilePolicy policy)
    {
        HashSet<string> ignored = new(StringComparer.Ordinal);
        foreach (string key in new[] { "SetupMode:Enabled", "ChannelSetup:Enabled" })
            if (configuration.GetValue<bool>(key)) ignored.Add(key);
        foreach (ContextPolicy context in policy.Contexts)
        {
            void Disable(string name, bool value, Action clear)
            {
                if (value) ignored.Add("FilePolicy:Contexts:*:" + name);
                clear();
            }
            Disable(nameof(context.AutoExecuteFolderCreateTestMode), context.AutoExecuteFolderCreateTestMode, () => context.AutoExecuteFolderCreateTestMode = false);
            Disable(nameof(context.AutoExecuteRenameTestMode), context.AutoExecuteRenameTestMode, () => context.AutoExecuteRenameTestMode = false);
            Disable(nameof(context.AutoExecuteMoveTestMode), context.AutoExecuteMoveTestMode, () => context.AutoExecuteMoveTestMode = false);
            Disable(nameof(context.AutoExecuteGroupShareTestMode), context.AutoExecuteGroupShareTestMode, () => context.AutoExecuteGroupShareTestMode = false);
            Disable(nameof(context.SharingRosterDiscoveryOnly), context.SharingRosterDiscoveryOnly, () => context.SharingRosterDiscoveryOnly = false);
            Disable(nameof(context.UploadMappingDiagnosticEnabled), context.UploadMappingDiagnosticEnabled, () => context.UploadMappingDiagnosticEnabled = false);
            Disable(nameof(context.SectionTwoEnabled), context.SectionTwoEnabled, () => context.SectionTwoEnabled = false);
            Disable(nameof(context.GroupShareEnabled), context.GroupShareEnabled, () => context.GroupShareEnabled = false);
            Disable(nameof(context.AutoChannelFolderCreateEnabled), context.AutoChannelFolderCreateEnabled, () => context.AutoChannelFolderCreateEnabled = false);
            Disable(nameof(context.AutoChannelRenameEnabled), context.AutoChannelRenameEnabled, () => context.AutoChannelRenameEnabled = false);
            Disable(nameof(context.AutoChannelMoveEnabled), context.AutoChannelMoveEnabled, () => context.AutoChannelMoveEnabled = false);
            Disable(nameof(context.AutoChannelArtifactCreateEnabled), context.AutoChannelArtifactCreateEnabled, () => context.AutoChannelArtifactCreateEnabled = false);
            Disable(nameof(context.AutoChannelContentEditEnabled), context.AutoChannelContentEditEnabled, () => context.AutoChannelContentEditEnabled = false);
        }
        // At most fifteen fixed, allowlisted names; never include context IDs, values or arbitrary keys.
        return ignored.Count == 0 ? "" : "Ignored retired WorkIQ flags; effective values are false: " +
            string.Join(", ", ignored.Order(StringComparer.Ordinal)) +
            ". Saved settings and audience ACLs are unchanged; these flags grant no native mutation consent.";
    }
}
