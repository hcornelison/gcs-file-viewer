using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;

namespace GcsFileViewer.ViewModels;

/// <summary>The session cost estimate: a one-line summary for the status bar and a breakdown behind it.</summary>
public partial class CostViewModel : ObservableObject
{
    private readonly UsageTracker _usage;

    [ObservableProperty]
    private string _summary = "";

    /// <summary>Set when cold-storage objects were removed, since their early deletion fees aren't in the estimate.</summary>
    [ObservableProperty]
    private string? _warning;

    public CostViewModel(UsageTracker usage)
    {
        _usage = usage;
        Refresh();
    }

    public ObservableCollection<CostLine> Lines { get; } = [];

    public string PricingNote { get; } =
        $"An estimate from Google's list prices for single-region buckets as of {GcsPricing.AsOf:yyyy-MM-dd}. " +
        "It covers the requests and downloads this app made since it started, not storage itself, and ignores " +
        "the free tier and any discounts. Public buckets opened without signing in are billed to their owner and " +
        "aren't counted. Your actual charges are in Cloud Billing.";

    /// <summary>Rebuilds the summary and breakdown from what has been tallied.</summary>
    public void Refresh()
    {
        var usage = _usage.Snapshot();
        Summary = "Est. cost: " + FormatCost(usage.TotalCost);

        Lines.Clear();
        Lines.Add(new CostLine("List, upload and copy requests", $"{usage.ClassAOperations:N0}", FormatCost(usage.ClassACost, precise: true)));
        Lines.Add(new CostLine("Download and read requests", $"{usage.ClassBOperations:N0}", FormatCost(usage.ClassBCost, precise: true)));
        Lines.Add(new CostLine("Delete requests", $"{usage.FreeOperations:N0}", "Free"));
        Lines.Add(new CostLine("Downloaded", BrowserItem.FormatSize(usage.BytesDownloaded), FormatCost(usage.DownloadCost, precise: true)));
        if (usage.BytesRetrieved > 0)
        {
            Lines.Add(new CostLine("Retrieved from cold storage", BrowserItem.FormatSize(usage.BytesRetrieved), FormatCost(usage.RetrievalCost, precise: true)));
        }

        Lines.Add(new CostLine("Total", "", FormatCost(usage.TotalCost, precise: true)));

        Warning = usage.ColdObjectsRemoved switch
        {
            0 => null,
            1 => "1 object in Nearline, Coldline or Archive storage was deleted, moved or replaced. " +
                 "If it was newer than its minimum storage duration, an early deletion fee applies that isn't included here.",
            var n => $"{n:N0} objects in Nearline, Coldline or Archive storage were deleted, moved or replaced. " +
                     "Any newer than their minimum storage duration carry early deletion fees that aren't included here.",
        };
    }

    [RelayCommand]
    private void Reset()
    {
        _usage.Reset();
        Refresh();
    }

    // Whole cents for the status bar ("<$0.01" for anything smaller); up to four decimals in the breakdown.
    private static string FormatCost(decimal cost, bool precise = false)
    {
        if (precise)
        {
            return cost > 0 && cost < 0.0001m ? $"<${0.0001m:0.0000}" : $"${cost:0.00##}";
        }

        return cost > 0 && cost < 0.01m ? $"<${0.01m:0.00}" : $"${cost:0.00}";
    }
}
