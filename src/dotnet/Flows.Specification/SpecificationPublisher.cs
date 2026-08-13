using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Engine;

namespace Flows.Specification;

/// <summary>Outcome of a publish attempt: either every file landed and the manifest was written, or nothing in <c>specs/active/</c> was touched.</summary>
public sealed record PublishResult(bool Success, string? Error, IReadOnlyDictionary<string, string>? Digests)
{
    public static PublishResult Ok(IReadOnlyDictionary<string, string> digests) => new(true, null, digests);
    public static PublishResult Blocked(string error) => new(false, error, null);
}

/// <summary>
/// Publishes the four accepted Specification documents to <c>specs/active/</c> (blueprint
/// 0004 §6 "Publicação secura"). This is the only writer of that directory this flow ever
/// uses: renders accepted documents into a staging directory sibling to the destination,
/// digests each file plus a canonical manifest digest, refuses to touch the destination at
/// all if it contains anything unexpected, and — when clear — copies in exactly the four
/// known filenames and writes the ownership manifest last.
///
/// Deliberately never deletes or globs <c>specs/active/</c>: every write to that directory
/// is a named <see cref="File.Copy(string, string, bool)"/> of one of the four filenames this
/// type knows about, nothing else.
/// </summary>
public static class SpecificationPublisher
{
    // Repo-relative constant, matching blueprint 0004 §6's literal file tree — not derived
    // from HarnessConfig.DocsFolder (wiring the two together is a later concern).
    private const string DestinationDir = "specs/active";
    private const string ManifestPath = ".harness/specification/active/publish-manifest.json";

    private const string PrdFilename = "00-prd.md";
    private const string SrsFilename = "10-software-requirements-specification.md";
    private const string SddFilename = "20-software-design-document.md";
    private const string ReadinessFilename = "30-readiness-handoff.md";

    private static readonly string[] ExpectedFilenames = [PrdFilename, SrsFilename, SddFilename, ReadinessFilename];

    /// <summary>
    /// Renders the four accepted documents into their published filenames, without touching
    /// disk. Extracted out of <see cref="Publish"/> so a pre-publish gate (the Development
    /// readiness evaluator, run from <c>SpecificationTasks.Approve</c>) can render the exact
    /// same content <see cref="Publish"/> will stage — e.g. for a byte-budget check — without
    /// re-implementing the filename map or diverging from the real renderer.
    /// </summary>
    public static Dictionary<string, string> RenderAll(PrdDocument prd, SoftwareSpecification srs, SoftwareDesignDocument sdd, ReadinessVerdict readiness) => new()
    {
        [PrdFilename] = SpecificationRenderer.RenderPrd(prd),
        [SrsFilename] = SpecificationRenderer.RenderSrs(srs),
        [SddFilename] = SpecificationRenderer.RenderSdd(sdd),
        [ReadinessFilename] = SpecificationRenderer.RenderReadiness(readiness),
    };

    /// <summary>
    /// Renders, stages, validates and — only if validation passes — publishes the four
    /// accepted documents. Returns <see cref="PublishResult.Blocked"/> (with the destination
    /// left completely untouched) if the destination contains any file this flow doesn't
    /// recognize as its own from a previous publish. After the manifest is written, re-reads
    /// the destination through the REAL <see cref="Harness.Engine.DocsReader.Read"/> — the
    /// same function Development uses — via <see cref="VerifyPostcondition"/>, and only
    /// reports success if that read-back matches byte-for-byte (blueprint 0004 §6 item 6
    /// "Executar DocsReader.Read"). The copy and manifest write themselves are NOT rolled back
    /// on a postcondition failure — this method's job is only to determine whether the run may
    /// be marked <c>completed</c>; the caller turns any <see cref="PublishResult.Blocked"/>
    /// into <c>publish_blocked</c> either way.
    /// </summary>
    public static PublishResult Publish(PrdDocument prd, SoftwareSpecification srs, SoftwareDesignDocument sdd, ReadinessVerdict readiness)
    {
        var rendered = RenderAll(prd, srs, sdd, readiness);

        var stagingDir = $"{DestinationDir}.staging-{Guid.NewGuid():N}";

        try
        {
            Directory.CreateDirectory(stagingDir);

            var digests = new Dictionary<string, string>();
            foreach (var filename in ExpectedFilenames)
            {
                var content = rendered[filename];
                File.WriteAllText(Path.Combine(stagingDir, filename), content);
                digests[filename] = Digest(content);
            }

            // Ownership check BEFORE anything in the destination is touched. An empty/missing
            // previous manifest means nothing is "owned" yet — so even a pre-existing file that
            // happens to share one of the four expected names blocks a first publish, unless
            // the destination is missing or empty (the normal fresh-publish case).
            var previouslyOwned = (ReadManifest()?.OwnedFiles ?? []).ToHashSet();

            if (Directory.Exists(DestinationDir))
            {
                foreach (var existingPath in Directory.GetFiles(DestinationDir))
                {
                    var existingName = Path.GetFileName(existingPath);
                    if (!ExpectedFilenames.Contains(existingName))
                        return PublishResult.Blocked($"unrecognized file '{existingName}' exists in '{DestinationDir}'; publish blocked.");

                    if (!previouslyOwned.Contains(existingName))
                        return PublishResult.Blocked($"file '{existingName}' exists in '{DestinationDir}' but is not owned by a previous publish of this flow; publish blocked.");
                }
            }

            // Validation passed: copy the four known files in — never a directory-level
            // delete/glob, only these exact, known filenames — then write the manifest last.
            Directory.CreateDirectory(DestinationDir);
            foreach (var filename in ExpectedFilenames)
                File.Copy(Path.Combine(stagingDir, filename), Path.Combine(DestinationDir, filename), overwrite: true);

            var manifestDigest = Digest(string.Join("|", ExpectedFilenames.Select(f => digests[f])));
            var manifest = new PublishManifest(ExpectedFilenames, digests, manifestDigest, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            AtomicIO.WriteAllTextAtomic(ManifestPath, JsonSerializer.Serialize(manifest, SpecificationJsonContext.Default.PublishManifest));

            return VerifyPostcondition(digests);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
    }

    /// <summary>
    /// Independent, standalone-callable postcondition check (blueprint 0004 §6 item 6): calls
    /// the REAL <see cref="Harness.Engine.DocsReader.Read"/> against <see cref="DestinationDir"/>
    /// — not a re-implementation of its file-listing/truncation logic — and byte-compares its
    /// output against what <see cref="Publish"/> just wrote.
    /// <list type="bullet">
    /// <item>names and order: <see cref="Harness.Engine.DocsReader.Read"/>'s returned file list
    /// must equal <see cref="ExpectedFilenames"/> exactly — a mismatch (fewer files from a
    /// silent mid-run truncation, wrong order, an unexpected extra name) is reported by name.</item>
    /// <item>content, per file: the on-disk file is re-read directly (independent of
    /// DocsReader) and its digest recomputed with the same <see cref="Digest"/> helper
    /// <see cref="Publish"/> used when staging — a mismatch against
    /// <paramref name="expectedDigests"/> means the on-disk file no longer matches what was
    /// actually published (tampered, or a partial/corrupted write).</item>
    /// <item>content, via DocsReader: DocsReader's own concatenated return value must contain
    /// each file's full raw on-disk text as a substring — confirms DocsReader picked up the
    /// complete, untruncated content of every file (a silent mid-file truncation from
    /// <c>docsMaxChars</c> would drop the tail of that file's text from the concatenation).
    /// Compared with trailing whitespace trimmed on both sides: <c>DocsReader.Read</c> itself
    /// does a final <c>TrimEnd()</c> over the WHOLE concatenation (for presentation), which can
    /// strip a few trailing newline characters off the last file in alphabetical order — that
    /// is not a truncation, so trimming here avoids a false positive while a genuine mid-file
    /// truncation (which cuts off real content, not just trailing whitespace) still fails the
    /// substring check.</item>
    /// </list>
    /// Independently callable (not buried as a private local function reachable only through a
    /// full <see cref="Publish"/> call) so it can be exercised directly against a deliberately
    /// tampered on-disk state.
    /// </summary>
    public static PublishResult VerifyPostcondition(IReadOnlyDictionary<string, string> expectedDigests)
    {
        var (content, files) = DocsReader.Read(DestinationDir);

        if (!files.SequenceEqual(ExpectedFilenames))
            return PublishResult.Blocked(
                $"postcondition failed: DocsReader.Read('{DestinationDir}') returned files [{string.Join(", ", files)}], expected [{string.Join(", ", ExpectedFilenames)}].");

        foreach (var filename in ExpectedFilenames)
        {
            var path = Path.Combine(DestinationDir, filename);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return PublishResult.Blocked($"postcondition failed: could not read '{path}': {ex.Message}");
            }

            var actualDigest = Digest(text);
            if (!expectedDigests.TryGetValue(filename, out var expectedDigest) || actualDigest != expectedDigest)
                return PublishResult.Blocked(
                    $"postcondition failed: '{filename}' on-disk digest '{actualDigest}' does not match the digest recorded at publish time '{expectedDigest}'.");

            if (!content.Contains(text.TrimEnd()))
                return PublishResult.Blocked(
                    $"postcondition failed: DocsReader.Read('{DestinationDir}')'s content does not contain the full on-disk text of '{filename}' (possible truncation).");
        }

        return PublishResult.Ok(expectedDigests);
    }

    private static PublishManifest? ReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
                return null;

            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize(json, SpecificationJsonContext.Default.PublishManifest);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationPublisher] failed to read manifest: {ex.Message}");
            return null;
        }
    }

    // Same "sha256:<lowercase hex>" format SpecificationStore uses for accepted documents —
    // mirrored rather than shared since SpecificationStore's Digest() is private to that type.
    private static string Digest(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}
