using GcsFileViewer.Pricing;

namespace GcsFileViewer.Tests;

public class GcsPricingTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(StorageTier.Standard, 0.005)]
    [InlineData(StorageTier.Nearline, 0.01)]
    [InlineData(StorageTier.Coldline, 0.02)]
    [InlineData(StorageTier.Archive, 0.05)]
    public void ClassA_is_priced_per_thousand_by_tier(StorageTier tier, double perThousand) =>
        Assert.Equal((decimal)perThousand, GcsPricing.ClassA(tier, multiRegion: false, 1000));

    [Fact]
    public void ClassA_costs_double_in_multi_region_buckets() =>
        Assert.Equal(0.01m, GcsPricing.ClassA(StorageTier.Standard, multiRegion: true, 1000));

    [Theory]
    [InlineData(StorageTier.Standard, 0.0004)]
    [InlineData(StorageTier.Nearline, 0.001)]
    [InlineData(StorageTier.Coldline, 0.01)]
    [InlineData(StorageTier.Archive, 0.05)]
    public void ClassB_is_priced_per_thousand_by_tier(StorageTier tier, double perThousand) =>
        Assert.Equal((decimal)perThousand, GcsPricing.ClassB(tier, 1000));

    [Theory]
    [InlineData(StorageTier.Standard, 0)]
    [InlineData(StorageTier.Nearline, 0.01)]
    [InlineData(StorageTier.Coldline, 0.02)]
    [InlineData(StorageTier.Archive, 0.05)]
    public void Retrieval_is_priced_per_gibibyte_for_cold_tiers_only(StorageTier tier, double perGiB) =>
        Assert.Equal((decimal)perGiB, GcsPricing.Retrieval(tier, GiB));

    [Fact]
    public void Download_is_priced_per_gibibyte()
    {
        Assert.Equal(0.12m, GcsPricing.Download(GiB));
        Assert.Equal(0.06m, GcsPricing.Download(GiB / 2));
        Assert.Equal(0m, GcsPricing.Download(0));
    }

    [Theory]
    [InlineData(null, StorageTier.Standard)]
    [InlineData("", StorageTier.Standard)]
    [InlineData("standard", StorageTier.Standard)]
    [InlineData("REGIONAL", StorageTier.Standard)]
    [InlineData("MULTI_REGIONAL", StorageTier.Standard)]
    [InlineData("something-new", StorageTier.Standard)]
    [InlineData("nearline", StorageTier.Nearline)]
    [InlineData("DURABLE_REDUCED_AVAILABILITY", StorageTier.Nearline)]
    [InlineData("COLDLINE", StorageTier.Coldline)]
    [InlineData("ARCHIVE", StorageTier.Archive)]
    public void Storage_class_names_map_to_tiers_including_legacy_and_missing_ones(string? storageClass, StorageTier expected) =>
        Assert.Equal(expected, StorageTiers.Parse(storageClass));

    [Theory]
    [InlineData(StorageTier.Standard, false)]
    [InlineData(StorageTier.Nearline, true)]
    [InlineData(StorageTier.Coldline, true)]
    [InlineData(StorageTier.Archive, true)]
    public void Every_tier_but_standard_is_cold(StorageTier tier, bool expected) =>
        Assert.Equal(expected, tier.IsCold());

    [Theory]
    [InlineData("US", true)]
    [InlineData("EU", true)]
    [InlineData("NAM4", true)]
    [InlineData("us-east1", false)]
    [InlineData("EUROPE-WEST2", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsMultiRegion_tells_regions_from_multi_and_dual_regions(string? location, bool expected) =>
        Assert.Equal(expected, GcsPricing.IsMultiRegion(location));
}
