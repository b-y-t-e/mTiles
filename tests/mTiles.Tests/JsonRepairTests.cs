using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The one repair a block written by hand is given, and the line it will not cross.
/// </summary>
/// <remarks>
/// Every case here arrived from a real answer. The rule being pinned is the hard half — when a double
/// quote inside a string ends that string and when it is a character somebody typed — because getting
/// it wrong in the permissive direction turns a broken answer into a differently broken one, and in the
/// strict direction leaves the user reading raw braces in the transcript.
/// </remarks>
public class JsonRepairTests
{
    [Fact]
    public void A_polish_quotation_inside_a_value_no_longer_ends_the_value()
    {
        // Measured live, 2026-09-03: a clarify round quoting the plan it was reading. The closing mark
        // of „…" is an ordinary double quote and it is followed by a comma, which is exactly what the
        // end of a value looks like — so the comma is trusted only when a value follows it.
        var broken = "{\"needsClarification\":true,\"questions\":[{\"question\":\"Co dalej?\"," +
                     "\"why\":\"Plan mówi „z pytaniem do użytkownika\", ale nie mówi gdzie.\"}]}";

        var clarify = GoalResponseParser.ParseClarify(broken);

        Assert.True(clarify.WasStructured);
        Assert.True(clarify.NeedsClarification);
        var only = Assert.Single(clarify.Questions);
        Assert.Equal("Co dalej?", only.Question);
        Assert.Contains("„z pytaniem do użytkownika\", ale", only.Why);
    }

    [Fact]
    public void A_quoted_line_of_code_inside_a_finding_no_longer_costs_the_review()
    {
        // The review's own case, which used to need an AI round to repair. The repair is free and runs
        // inside the parser, so both phases get it and neither pays for it.
        var broken = "{\"goalMet\":true,\"findings\":[{\"severity\":\"warning\",\"title\":\"quoting\"," +
                     "\"detail\":\"throw new ExportException($\"Nie ma pliku\");\"}]}";

        var review = GoalResponseParser.ParseReview(broken);

        Assert.True(review.WasStructured);
        Assert.True(review.GoalMet);
        Assert.Equal("quoting", Assert.Single(review.Findings).Title);
    }

    [Fact]
    public void A_raw_newline_inside_a_value_is_escaped_rather_than_thrown_away()
    {
        var broken = "{\"goalMet\":false,\"findings\":[{\"title\":\"two lines\",\"detail\":\"first\nsecond\"}]}";

        var review = GoalResponseParser.ParseReview(broken);

        Assert.True(review.WasStructured);
        Assert.Equal("first\nsecond", Assert.Single(review.Findings).Detail);
    }

    [Fact]
    public void Valid_json_is_left_exactly_as_it_is()
    {
        // The repair is asked only after a refusal, and it answers null when it changed nothing — so a
        // block that already parses never travels through it at all.
        Assert.Null(JsonRepair.Repaired("{\"a\":\"b\",\"c\":[1,2]}"));
        Assert.Null(JsonRepair.Repaired("{\"a\":\"he said \\\"hi\\\"\"}"));
        Assert.Null(JsonRepair.Repaired(""));
        Assert.Null(JsonRepair.Repaired(null));
    }

    [Theory]
    // A quote followed by a key separator, a closer, or a comma with a value after it, ends the string.
    [InlineData("{\"a\":\"x\",\"b\":\"y\"}", "x", "y")]
    [InlineData("{\"a\":\"x\" , \"b\":\"y\"}", "x", "y")]
    // …and one followed by prose does not, however much it looks like the end of a value.
    [InlineData("{\"a\":\"powiedział \"tak\", potem wyszedł\",\"b\":\"y\"}",
        "powiedział \"tak\", potem wyszedł", "y")]
    public void A_quote_ends_a_string_only_where_the_grammar_could_go_on(string json, string a, string b)
    {
        var root = GoalResponseParser.ExtractJson(json);

        Assert.NotNull(root);
        Assert.Equal(a, root!.Value.GetProperty("a").GetString());
        Assert.Equal(b, root.Value.GetProperty("b").GetString());
    }

    [Fact]
    public void An_answer_cut_off_part_way_through_is_not_finished_for_it()
    {
        // The brackets that would make this legal are content nobody wrote. Repairing it would hand
        // the loop a review with findings quietly missing from it; failing leaves the prose fallback,
        // which is at least visibly what happened.
        var truncated = "{\"goalMet\":false,\"findings\":[{\"title\":\"half a fin";

        Assert.False(GoalResponseParser.ParseReview(truncated).WasStructured);
    }

    [Fact]
    public void A_broken_clarification_still_earns_the_salvage_round()
    {
        // The trigger the clarify phase did not have: its keys were missing from LooksLikeJson, so a
        // block broken past repairing had no second line at all and went into the transcript raw.
        Assert.True(GoalResponseParser.LooksLikeJson("{\"needsClarification\":true,\"questions\":[{"));
        Assert.True(GoalResponseParser.LooksLikeJson("{\"questions\": [\"why"));
        Assert.False(GoalResponseParser.LooksLikeJson("Which config file holds the port?"));
    }
}
