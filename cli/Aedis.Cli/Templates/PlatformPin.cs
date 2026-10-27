using System.Text.RegularExpressions;
using Aedis.Cli.Validation;

namespace Aedis.Cli.Templates;

/// <summary>
///     Pin de um workflow ou action do aedis-platform nos arquivos gerados: <c>&lt;sha&gt; # vX.Y.Z</c>, o SHA do commit
///     de uma tag de release com a tag no comentário, que é o que o Dependabot acompanha e o que o controle PLT-22
///     exige. A ref pedida (tag <c>vX.Y.Z</c>, major <c>v1</c>, branch ou SHA) é resolvida com <c>git ls-remote</c>.
/// </summary>
public static partial class PlatformPin
{
    /// <summary>Repositório da plataforma consultado pelo <c>git ls-remote</c>.</summary>
    public const string Repository = "https://github.com/aedis-build/aedis-platform";

    /// <summary>
    ///     Resolve <paramref name="platformRef" /> em <c>&lt;sha&gt; # &lt;tag&gt;</c>. Um SHA é devolvido como está; sem
    ///     rede ou com uma ref desconhecida, devolve a própria ref e avisa, porque o pin fica fora da política.
    /// </summary>
    public static string Resolve(string platformRef) {
        if (Sha().IsMatch(platformRef)) return platformRef;
        try {
            var (code, stdout, _) = ProcessRunner.RunAsync("git", ["ls-remote", "--tags", "--heads", Repository], CancellationToken.None).GetAwaiter().GetResult();
            if (code == 0 && Pick(stdout, platformRef) is { } pin) return pin;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) {
            // git ausente ou sem rede: o aviso abaixo diz o que fazer.
        }

        Console.Error.WriteLine($"aviso: {platformRef} não foi resolvida no aedis-platform; os workflows ficam com @{platformRef}. Troque pelo SHA de uma tag vX.Y.Z (controle PLT-22).");
        return platformRef;
    }

    /// <summary>
    ///     Escolhe, na saída de <c>git ls-remote --tags --heads</c>, o pin da ref: a tag <c>vX.Y.Z</c> exata; para um
    ///     major (<c>v1</c>), a release mais alta dele; para uma branch, o SHA dela com o nome no comentário (fora da
    ///     política, mas rastreável); <c>null</c> quando a ref não existe.
    /// </summary>
    public static string? Pick(string lsRemote, string platformRef) {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lsRemote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var parts = line.Split('\t');
            if (parts.Length != 2) continue;
            // A entrada ^{} é o commit apontado por uma tag anotada; prevalece sobre o objeto da tag.
            var peeled = parts[1].EndsWith("^{}", StringComparison.Ordinal);
            var name = peeled ? parts[1][..^3] : parts[1];
            if (peeled || !refs.ContainsKey(name)) refs[name] = parts[0];
        }

        if (ReleaseTag().IsMatch(platformRef) && refs.TryGetValue($"refs/tags/{platformRef}", out var exact)) return $"{exact} # {platformRef}";
        if (Major().IsMatch(platformRef)) {
            var latest = refs.Keys
                .Select(r => (Name: r[r.LastIndexOf('/')..][1..], Match: ReleaseTag().Match(r[r.LastIndexOf('/')..][1..])))
                .Where(t => t.Match.Success && t.Name.StartsWith(platformRef + ".", StringComparison.Ordinal))
                .OrderByDescending(t => Version.Parse(t.Name[1..]))
                .Select(t => t.Name)
                .FirstOrDefault();
            if (latest is not null) return $"{refs[$"refs/tags/{latest}"]} # {latest}";
        }

        return refs.TryGetValue($"refs/heads/{platformRef}", out var head) ? $"{head} # {platformRef}" : null;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex Major();
}
