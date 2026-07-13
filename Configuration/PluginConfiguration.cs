using MediaBrowser.Model.Plugins;

namespace JellyNomad.Configuration;

public class ChannelConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool AllowCommercials { get; set; }
    public bool AllowTrailers { get; set; }
    public string? MaxTrailerRating { get; set; }
    public int BreakItemCount { get; set; } = 1;
    public int EpisodesPerBreak { get; set; } = 1;
    public int HoursBetweenRepeat { get; set; }
    public int SuccessiveEpisodesAllowed { get; set; } = 1;
    public string ChannelImage { get; set; } = string.Empty;
    public string[] SelectedSeriesIds { get; set; } = [];
    public List<string> SelectedSeriesNames { get; set; } = [];
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Channels = [];
    }

    public string CommercialsPath { get; set; } = string.Empty;
    public ChannelConfiguration[] Channels { get; set; }
}
