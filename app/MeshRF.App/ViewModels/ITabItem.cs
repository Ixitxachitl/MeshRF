// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.App.ViewModels;

/// <summary>
/// Common surface for anything shown as a tab in the channel/conversation
/// TabControl. Lets channels and direct-message conversations coexist in one
/// <c>ItemsSource</c> while sharing a header template.
/// </summary>
public interface ITabItem
{
    /// <summary>Text shown on the tab header.</summary>
    string TabHeader { get; }

    /// <summary>True if the user may close this tab (DM conversations); channels
    /// are permanent and return false.</summary>
    bool CanClose { get; }
}
