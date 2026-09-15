using Avalonia;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The one thing about <see cref="Win32Rendering"/> that is an opinion rather than a table: what the
/// log says when <c>MTILES_GPU</c> is set under a renderer that never asks for an adapter.
/// </summary>
public class Win32RenderingTests
{
    [Theory]
    [InlineData("amd", Win32RenderingMode.Vulkan)]
    [InlineData("1", Win32RenderingMode.Wgl)]
    [InlineData("nvidia", Win32RenderingMode.Software)]
    public void A_gpu_asked_for_outside_angle_is_said_to_be_ignored(string gpu, Win32RenderingMode rendering)
    {
        var line = Win32Rendering.ExplainGpuSelection(gpu, rendering);

        Assert.Contains("ignored", line);
        Assert.Contains(gpu, line);
        Assert.Contains(rendering.ToString(), line);
    }

    [Fact]
    public void Under_angle_the_adapter_list_is_promised_rather_than_a_refusal()
    {
        var line = Win32Rendering.ExplainGpuSelection("amd", Win32RenderingMode.AngleEgl);

        Assert.DoesNotContain("ignored", line);
        Assert.Contains("amd", line);
    }

    [Fact]
    public void With_no_gpu_asked_for_the_line_still_says_which_adapter_is_used()
    {
        Assert.Contains("default adapter", Win32Rendering.ExplainGpuSelection(null, Win32RenderingMode.Vulkan));
        Assert.Contains("Avalonia chooses", Win32Rendering.ExplainGpuSelection(null, Win32RenderingMode.AngleEgl));
    }

    private static readonly string[] Adapters = ["AMD Radeon 610M", "NVIDIA GeForce RTX 4060"];

    [Theory]
    [InlineData(null, 0)]
    [InlineData("1", 1)]
    [InlineData("nvidia", 1)]
    [InlineData(" amd ", 0)]
    public void A_gpu_that_names_an_adapter_is_used_without_complaint(string? wanted, int expected)
    {
        var choice = Win32Rendering.ChooseAdapter(Adapters, wanted);

        Assert.Equal(expected, choice.Index);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void An_index_out_of_range_is_not_read_as_part_of_a_name()
    {
        var choice = Win32Rendering.ChooseAdapter(["Intel UHD", "AMD Radeon 620M"], "2");

        Assert.Equal(0, choice.Index);
        Assert.Contains("not an adapter index", choice.Problem);
    }

    [Fact]
    public void A_name_nothing_matches_says_so()
    {
        var choice = Win32Rendering.ChooseAdapter(Adapters, "intel");

        Assert.Equal(0, choice.Index);
        Assert.Contains("matches no adapter", choice.Problem);
    }
}
