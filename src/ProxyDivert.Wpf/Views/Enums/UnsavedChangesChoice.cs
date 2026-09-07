namespace ProxyDivert.Wpf.Views.Enums;

/// <summary>What the user answered when asked about closing an editor with unsaved changes.</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Stay in the editor. Also what closing the question itself means.</summary>
    Cancel,

    /// <summary>Close and throw the edit away.</summary>
    Discard,

    /// <summary>Close and keep the edit.</summary>
    Save,
}
