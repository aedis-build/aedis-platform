using Aedis.Cli.Templates;
using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>Pin dos workflows da plataforma (<c>&lt;sha&gt; # vX.Y.Z</c>) e o controle PLT-22 que o exige.</summary>
public sealed class PlatformPinTests
{
    private const string LsRemote =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n" +
        "1111111111111111111111111111111111111111\trefs/tags/v1\n" +
        "2222222222222222222222222222222222222222\trefs/tags/v1^{}\n" +
        "3333333333333333333333333333333333333333\trefs/tags/v1.0.0\n" +
        "4444444444444444444444444444444444444444\trefs/tags/v1.0.0^{}\n" +
        "5555555555555555555555555555555555555555\trefs/tags/v1.2.0\n" +
        "6666666666666666666666666666666666666666\trefs/tags/v1.2.0^{}\n" +
        "7777777777777777777777777777777777777777\trefs/tags/v1.10.1^{}\n" +
        "8888888888888888888888888888888888888888\trefs/tags/v2.0.0^{}\n" +
        "9999999999999999999999999999999999999999\trefs/tags/chart-aedis-app-v0.3.0\n";

    [Theory]
    [InlineData("v1.2.0", "6666666666666666666666666666666666666666 # v1.2.0")]
    [InlineData("v1", "7777777777777777777777777777777777777777 # v1.10.1")]
    [InlineData("v2", "8888888888888888888888888888888888888888 # v2.0.0")]
    [InlineData("main", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa # main")]
    [InlineData("v3", null)]
    [InlineData("v9.9.9", null)]
    public void Escolhe_o_sha_da_tag_de_release(string platformRef, string? expected) =>
        PlatformPin.Pick(LsRemote, platformRef).Should().Be(expected);

    [Fact]
    public void Um_sha_e_devolvido_como_esta_sem_consultar_a_rede() {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        PlatformPin.Resolve(sha).Should().Be(sha);
    }

    [Fact]
    public void PLT22_aponta_os_pins_fora_de_uma_tag_de_release() {
        var root = Path.Combine(Path.GetTempPath(), "aedis-plt22-" + Guid.NewGuid().ToString("N"));
        var workflows = Path.Combine(root, ".github", "workflows");
        Directory.CreateDirectory(workflows);
        try {
            File.WriteAllText(Path.Combine(workflows, "ci.yml"), """
                jobs:
                  ok:
                    uses: aedis-build/aedis-platform/.github/workflows/aedis-ci.yml@0123456789abcdef0123456789abcdef01234567 # v1.2.3
                  branch:
                    uses: aedis-build/aedis-platform/.github/workflows/sast.yml@0123456789abcdef0123456789abcdef01234567 # main
                  major:
                    uses: aedis-build/aedis-platform/.github/workflows/validate.yml@v1
                  steps:
                    - uses: aedis-build/aedis-dast@0123456789abcdef0123456789abcdef01234567 # v1.0.0
                    - uses: aedis-build/aedis-release-cut@0123456789abcdef0123456789abcdef01234567 # repin to v1.0.0 once released
                    - uses: aedis-build/aedis-conformance@v1
                    - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567 # v7.0.1
                """);
            var offenders = ConformanceChecker.UnreleasedPlatformPins(root);

            offenders.Should().BeEquivalentTo([
                ".github/workflows/ci.yml:5 @0123456789abcdef0123456789abcdef01234567",
                ".github/workflows/ci.yml:7 @v1",
                ".github/workflows/ci.yml:10 @0123456789abcdef0123456789abcdef01234567",
                ".github/workflows/ci.yml:11 @v1"
            ]);
            ConformanceChecker.UnreleasedPlatformPins(Path.Combine(root, "sem-workflows")).Should().BeEmpty();
        }
        finally {
            Directory.Delete(root, true);
        }
    }
}
