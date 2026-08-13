using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flows.Specification;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// SpecificationPublisher's safety properties (blueprint 0004 §6 "Publicação segura"): stage
/// first, digest every file plus a canonical manifest digest, block entirely (no write to
/// <c>specs/active/</c> at all) if the destination holds anything this flow doesn't recognize
/// as its own from a previous publish, replace only a bundle this flow previously owned, and
/// write the manifest strictly last.
/// </summary>
public class SpecificationPublisherTests : IDisposable
{
    // "specs" folder relative to the test process's CWD — mirrors DevelopmentFlowTests'
    // SpecsDir convention; only these tests populate it, created/deleted per test.
    private static readonly string SpecsDir = Path.Combine(Directory.GetCurrentDirectory(), "specs");
    private static readonly string ActiveDir = Path.Combine(SpecsDir, "active");
    private const string ManifestPath = ".harness/specification/active/publish-manifest.json";

    private const string PrdFilename = "00-prd.md";
    private const string SrsFilename = "10-software-requirements-specification.md";
    private const string SddFilename = "20-software-design-document.md";
    private const string ReadinessFilename = "30-readiness-handoff.md";

    public SpecificationPublisherTests() => Clean();

    public void Dispose() => Clean();

    private static void Clean()
    {
        SpecificationStore.Reset(); // also removes publish-manifest.json (same directory)
        if (Directory.Exists(SpecsDir))
            Directory.Delete(SpecsDir, recursive: true);
    }

    private static PrdDocument ValidPrd(string vision = "Give every team visibility") => new(
        "iao/prd/v1", "sha256:idea", vision,
        [new Goal("OBJ-001", "Reduce lost work")],
        [new Metric("MET-001", "OBJ-001", "tasks tracked", "100%")],
        ["time tracking"], ["task board"], [], [], []);

    private static SoftwareSpecification ValidSrs() => new(
        "iao/srs/v1", "sha256:prd",
        [new Requirement("RF-001", ["OBJ-001"], "track tasks", [], ["AC-001"])],
        [],
        [new AcceptanceCriterion("AC-001", ["RF-001"], "a task exists", "it is listed", "it appears")],
        [], [],
        new DeliveryContract("webapi", "integration tests", true));

    private static SoftwareDesignDocument ValidSdd() => new(
        "iao/sdd/v1", "sha256:srs",
        [new Adr("ADR-001", "Use REST", "Expose a REST API", "Simplicity", ["RF-001"])],
        []);

    private static ReadinessVerdict ValidReadiness() => new(
        "READY",
        [new ReadinessSlice("SL-001", "core", "Track tasks", ["create task"], [], "a created task appears",
            ["RF-001"], ["ADR-001"], [], [], "user adds a task", "invalid input is rejected",
            "the task appears in the response", "webapi", "integration test")],
        [], []);

    private static string Sha256Hex(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // --- fresh publish -----------------------------------------------------------------

    [Fact]
    public void Publish_DestinoInexistente_EscreveOsQuatroArquivosEOManifestoPorUltimo()
    {
        var prd = ValidPrd();
        var srs = ValidSrs();
        var sdd = ValidSdd();
        var readiness = ValidReadiness();

        var result = SpecificationPublisher.Publish(prd, srs, sdd, readiness);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);

        var prdPath = Path.Combine(ActiveDir, PrdFilename);
        var srsPath = Path.Combine(ActiveDir, SrsFilename);
        var sddPath = Path.Combine(ActiveDir, SddFilename);
        var readinessPath = Path.Combine(ActiveDir, ReadinessFilename);

        Assert.True(File.Exists(prdPath));
        Assert.True(File.Exists(srsPath));
        Assert.True(File.Exists(sddPath));
        Assert.True(File.Exists(readinessPath));
        Assert.True(File.Exists(ManifestPath));

        // Rendered content matches SpecificationRenderer directly — the publisher must not
        // re-implement rendering.
        Assert.Equal(SpecificationRenderer.RenderPrd(prd), File.ReadAllText(prdPath));
        Assert.Equal(SpecificationRenderer.RenderSrs(srs), File.ReadAllText(srsPath));
        Assert.Equal(SpecificationRenderer.RenderSdd(sdd), File.ReadAllText(sddPath));
        Assert.Equal(SpecificationRenderer.RenderReadiness(readiness), File.ReadAllText(readinessPath));

        // The manifest's recorded digests match the SHA-256 of each document's actual
        // on-disk content.
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), SpecificationJsonContext.Default.PublishManifest)!;
        Assert.Equal(Sha256Hex(File.ReadAllText(prdPath)), manifest.FileDigests[PrdFilename]);
        Assert.Equal(Sha256Hex(File.ReadAllText(srsPath)), manifest.FileDigests[SrsFilename]);
        Assert.Equal(Sha256Hex(File.ReadAllText(sddPath)), manifest.FileDigests[SddFilename]);
        Assert.Equal(Sha256Hex(File.ReadAllText(readinessPath)), manifest.FileDigests[ReadinessFilename]);
        Assert.Equal([PrdFilename, SrsFilename, SddFilename, ReadinessFilename], manifest.OwnedFiles);
        Assert.NotNull(result.Digests);
        Assert.Equal(manifest.FileDigests[PrdFilename], result.Digests![PrdFilename]);

        // Ordering property: the manifest is written strictly after every document file.
        var manifestWriteTime = File.GetLastWriteTimeUtc(ManifestPath);
        Assert.True(manifestWriteTime >= File.GetLastWriteTimeUtc(prdPath));
        Assert.True(manifestWriteTime >= File.GetLastWriteTimeUtc(srsPath));
        Assert.True(manifestWriteTime >= File.GetLastWriteTimeUtc(sddPath));
        Assert.True(manifestWriteTime >= File.GetLastWriteTimeUtc(readinessPath));

        // No staging directory leaked next to the destination.
        Assert.Empty(Directory.GetDirectories(SpecsDir, "active.staging-*"));
    }

    [Fact]
    public void Publish_DestinoVazioSemManifestoAnterior_EPublicacaoNormal()
    {
        Directory.CreateDirectory(ActiveDir); // exists, but empty — the normal fresh-publish case

        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(ActiveDir, PrdFilename)));
    }

    // --- unrecognized file blocks the whole publish -------------------------------------

    [Fact]
    public void Publish_ArquivoDesconhecidoNoDestino_BloqueiaEDeixaSpecsActiveIntocado()
    {
        Directory.CreateDirectory(ActiveDir);
        var roguePath = Path.Combine(ActiveDir, "rogue.txt");
        File.WriteAllText(roguePath, "not mine");

        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        // Nothing was written: the rogue file survives untouched and none of the four
        // expected filenames were created.
        Assert.Equal("not mine", File.ReadAllText(roguePath));
        Assert.False(File.Exists(Path.Combine(ActiveDir, PrdFilename)));
        Assert.False(File.Exists(Path.Combine(ActiveDir, SrsFilename)));
        Assert.False(File.Exists(Path.Combine(ActiveDir, SddFilename)));
        Assert.False(File.Exists(Path.Combine(ActiveDir, ReadinessFilename)));
        Assert.False(File.Exists(ManifestPath));
        Assert.Single(Directory.GetFiles(ActiveDir));
    }

    [Fact]
    public void Publish_ArquivoEsperadoPresenteMasNaoPossuidoPorPublicacaoAnterior_Bloqueia()
    {
        // One of the four expected filenames already exists, but there is no manifest
        // recording that this flow ever put it there (e.g. hand-authored, or a first publish
        // onto a directory someone else populated).
        Directory.CreateDirectory(ActiveDir);
        var prdPath = Path.Combine(ActiveDir, PrdFilename);
        File.WriteAllText(prdPath, "# Hand-authored PRD, not mine");

        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.False(result.Success);
        Assert.Equal("# Hand-authored PRD, not mine", File.ReadAllText(prdPath));
        Assert.False(File.Exists(ManifestPath));
    }

    // --- replacing a bundle this flow previously owned ----------------------------------

    [Fact]
    public void Publish_SegundaPublicacaoDoMesmoFlow_SubstituiOsQuatroArquivosPossuidos()
    {
        var firstResult = SpecificationPublisher.Publish(ValidPrd("first vision"), ValidSrs(), ValidSdd(), ValidReadiness());
        Assert.True(firstResult.Success);

        var secondResult = SpecificationPublisher.Publish(ValidPrd("second vision"), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.True(secondResult.Success);
        var prdContent = File.ReadAllText(Path.Combine(ActiveDir, PrdFilename));
        Assert.Contains("second vision", prdContent);
        Assert.DoesNotContain("first vision", prdContent);

        var manifest = JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), SpecificationJsonContext.Default.PublishManifest)!;
        Assert.Equal(Sha256Hex(prdContent), manifest.FileDigests[PrdFilename]);
    }

    [Fact]
    public void Publish_BloqueioNaoAtualizaOManifestoAnterior()
    {
        var firstResult = SpecificationPublisher.Publish(ValidPrd("first vision"), ValidSrs(), ValidSdd(), ValidReadiness());
        Assert.True(firstResult.Success);
        var manifestBefore = File.ReadAllText(ManifestPath);

        // An unrecognized file appears in the destination alongside the flow's own files.
        File.WriteAllText(Path.Combine(ActiveDir, "rogue.txt"), "intruder");

        var blockedResult = SpecificationPublisher.Publish(ValidPrd("second vision"), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.False(blockedResult.Success);
        // The manifest from the first, legitimate publish is untouched.
        Assert.Equal(manifestBefore, File.ReadAllText(ManifestPath));
        Assert.Contains("first vision", File.ReadAllText(Path.Combine(ActiveDir, PrdFilename)));
    }

    // --- postcondition (blueprint 0004 §6 item 6: "Executar DocsReader.Read") ------------

    [Fact]
    public void Publish_CaminhoFeliz_SoRetornaSucessoAposPostconditionViaDocsReaderPassar()
    {
        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());

        Assert.True(result.Success);
        Assert.NotNull(result.Digests);

        // The real DocsReader.Read (the same function Development uses) picks up exactly the
        // four published files, in order.
        var (content, files) = DocsReader.Read("specs/active");
        Assert.Equal([PrdFilename, SrsFilename, SddFilename, ReadinessFilename], files);
        Assert.Contains(File.ReadAllText(Path.Combine(ActiveDir, PrdFilename)), content);

        // VerifyPostcondition, called independently against the just-published state, also
        // passes using the digests Publish returned.
        var postcondition = SpecificationPublisher.VerifyPostcondition(result.Digests!);
        Assert.True(postcondition.Success);
    }

    [Fact]
    public void VerifyPostcondition_ArquivoPublicadoAdulteradoNoDisco_BloqueiaNomeandoOArquivo()
    {
        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());
        Assert.True(result.Success);

        // Simulates external tampering that happens strictly between the copy and a later
        // postcondition check — not reachable mid-Publish() in a single-threaded synchronous
        // call without an artificial seam, so this exercises VerifyPostcondition directly
        // against a post-tamper on-disk state, using the digests from the original successful
        // publish (exactly what Publish() itself passes to VerifyPostcondition internally).
        File.WriteAllText(Path.Combine(ActiveDir, SrsFilename), "tampered content, not what was published");

        var postcondition = SpecificationPublisher.VerifyPostcondition(result.Digests!);

        Assert.False(postcondition.Success);
        Assert.NotNull(postcondition.Error);
        Assert.Contains(SrsFilename, postcondition.Error);
    }

    [Fact]
    public void VerifyPostcondition_ArquivoEsperadoAusenteNoDisco_BloqueiaComListaDivergente()
    {
        var result = SpecificationPublisher.Publish(ValidPrd(), ValidSrs(), ValidSdd(), ValidReadiness());
        Assert.True(result.Success);

        File.Delete(Path.Combine(ActiveDir, SddFilename));

        var postcondition = SpecificationPublisher.VerifyPostcondition(result.Digests!);

        Assert.False(postcondition.Success);
        Assert.NotNull(postcondition.Error);
    }
}
