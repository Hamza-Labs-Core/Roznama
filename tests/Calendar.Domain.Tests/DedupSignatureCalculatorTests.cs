using Calendar.Domain.Engines;

namespace Calendar.Domain.Tests;

public class DedupSignatureCalculatorTests
{
    [Fact]
    public void Same_holiday_across_calendars_produces_the_same_signature()
    {
        var a = DedupSignatureCalculator.Compute("New Year's Day", new DateOnly(2026, 1, 1), allDay: true);
        var b = DedupSignatureCalculator.Compute("new year's day", new DateOnly(2026, 1, 1), allDay: true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Emoji_and_punctuation_do_not_affect_the_signature()
    {
        var plain = DedupSignatureCalculator.Compute("Mom's Birthday", new DateOnly(2026, 5, 4), allDay: true);
        var noisy = DedupSignatureCalculator.Compute("🎉 Mom's Birthday!!!", new DateOnly(2026, 5, 4), allDay: true);

        Assert.Equal(plain, noisy);
    }

    [Fact]
    public void Different_dates_produce_different_signatures()
    {
        var jan = DedupSignatureCalculator.Compute("Holiday", new DateOnly(2026, 1, 1), allDay: true);
        var feb = DedupSignatureCalculator.Compute("Holiday", new DateOnly(2026, 2, 1), allDay: true);

        Assert.NotEqual(jan, feb);
    }

    [Fact]
    public void AllDay_flag_is_part_of_the_signature()
    {
        var allDay = DedupSignatureCalculator.Compute("Event", new DateOnly(2026, 3, 1), allDay: true);
        var timed = DedupSignatureCalculator.Compute("Event", new DateOnly(2026, 3, 1), allDay: false);

        Assert.NotEqual(allDay, timed);
    }

    [Fact]
    public void Optional_end_date_changes_the_signature_when_supplied()
    {
        var single = DedupSignatureCalculator.Compute("Trip", new DateOnly(2026, 6, 1), allDay: true);
        var ranged = DedupSignatureCalculator.Compute("Trip", new DateOnly(2026, 6, 1), allDay: true,
            endDate: new DateOnly(2026, 6, 5));

        Assert.NotEqual(single, ranged);
    }

    [Fact]
    public void Signature_is_lowercase_hex_sha256()
    {
        var sig = DedupSignatureCalculator.Compute("Anything", new DateOnly(2026, 1, 1), allDay: true);

        Assert.Equal(64, sig.Length);
        Assert.All(sig, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Theory]
    [InlineData("  New   Year  ", "new year")]
    [InlineData("Café—Déjà", "cafédéjà")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeTitle_collapses_whitespace_and_strips_symbols(string input, string expected) =>
        Assert.Equal(expected, DedupSignatureCalculator.NormalizeTitle(input));
}
