package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// chdirTemp isolates a test in a fresh temporary working directory — publishDocuments,
// verifyPostcondition and the SpecificationStore helpers all operate on paths relative to
// the process cwd (".harness/specification/active", "specs/active"), so each test needs its
// own sandbox instead of touching a real repo checkout or colliding with other tests.
func chdirTemp(t *testing.T) {
	t.Helper()
	original, err := os.Getwd()
	if err != nil {
		t.Fatalf("failed to get working directory: %v", err)
	}
	temp := t.TempDir()
	if err := os.Chdir(temp); err != nil {
		t.Fatalf("failed to chdir into temp dir: %v", err)
	}
	t.Cleanup(func() {
		_ = os.Chdir(original)
	})
}

func samplePublishBundle() map[string]string {
	rendered := map[string]string{}
	for _, name := range publishedFiles {
		rendered[name] = "# " + name + "\n\ncontent for " + name + "\n"
	}
	return rendered
}

func TestPublishDocuments_FreshDestination_SucceedsAndVerifiesPostcondition(t *testing.T) {
	chdirTemp(t)

	ok, errMsg := publishDocuments(samplePublishBundle())
	if !ok {
		t.Fatalf("expected publish to succeed, got error: %s", errMsg)
	}
	if errMsg != "" {
		t.Fatalf("expected no error message on success, got %q", errMsg)
	}

	for _, name := range publishedFiles {
		data, err := os.ReadFile(filepath.Join("specs/active", name))
		if err != nil {
			t.Fatalf("expected '%s' to be published: %v", name, err)
		}
		if string(data) == "" {
			t.Fatalf("published '%s' is empty", name)
		}
	}

	manifestRaw, err := os.ReadFile(path("publish-manifest.json"))
	if err != nil {
		t.Fatalf("expected publish-manifest.json to exist: %v", err)
	}
	if !strings.Contains(string(manifestRaw), `"publishedAt"`) {
		t.Fatalf("expected manifest to record publishedAt, got %s", manifestRaw)
	}
}

func TestPublishDocuments_UnrecognizedFileInDestination_Blocks(t *testing.T) {
	chdirTemp(t)
	if err := os.MkdirAll("specs/active", 0755); err != nil {
		t.Fatalf("setup failed: %v", err)
	}
	if err := os.WriteFile(filepath.Join("specs/active", "99-not-ours.md"), []byte("intruder"), 0644); err != nil {
		t.Fatalf("setup failed: %v", err)
	}

	ok, errMsg := publishDocuments(samplePublishBundle())
	if ok {
		t.Fatal("expected publish to be blocked by an unrecognized file")
	}
	if !strings.Contains(errMsg, "unrecognized file") {
		t.Fatalf("expected 'unrecognized file' in the error, got %q", errMsg)
	}

	// A blocked publish must leave the destination completely untouched.
	entries, _ := os.ReadDir("specs/active")
	if len(entries) != 1 {
		t.Fatalf("expected specs/active to be untouched, found %d entries", len(entries))
	}
}

func TestPublishDocuments_ExpectedFilenameNotOwned_Blocks(t *testing.T) {
	chdirTemp(t)
	if err := os.MkdirAll("specs/active", 0755); err != nil {
		t.Fatalf("setup failed: %v", err)
	}
	// A file that happens to share one of the four expected names, but with no
	// publish-manifest.json recording it as ours, must still block — first-publish safety.
	if err := os.WriteFile(filepath.Join("specs/active", publishedFiles[0]), []byte("someone else's prd"), 0644); err != nil {
		t.Fatalf("setup failed: %v", err)
	}

	ok, errMsg := publishDocuments(samplePublishBundle())
	if ok {
		t.Fatal("expected publish to be blocked by an unowned file")
	}
	if !strings.Contains(errMsg, "not owned by a previous publish") {
		t.Fatalf("expected 'not owned by a previous publish' in the error, got %q", errMsg)
	}
}

func TestPublishDocuments_SecondPublishOverOwnFiles_Succeeds(t *testing.T) {
	chdirTemp(t)

	if ok, errMsg := publishDocuments(samplePublishBundle()); !ok {
		t.Fatalf("first publish failed: %s", errMsg)
	}

	updated := samplePublishBundle()
	updated[publishedFiles[0]] = "# updated prd\n"
	ok, errMsg := publishDocuments(updated)
	if !ok {
		t.Fatalf("expected a republish over previously-owned files to succeed, got: %s", errMsg)
	}

	data, err := os.ReadFile(filepath.Join("specs/active", publishedFiles[0]))
	if err != nil {
		t.Fatalf("failed to read republished file: %v", err)
	}
	if string(data) != updated[publishedFiles[0]] {
		t.Fatalf("expected republished content to win, got %q", data)
	}
}

func TestVerifyPostcondition_TamperedFileAfterPublish_Fails(t *testing.T) {
	chdirTemp(t)

	rendered := samplePublishBundle()
	ok, errMsg := publishDocuments(rendered)
	if !ok {
		t.Fatalf("setup publish failed: %s", errMsg)
	}

	// Tamper with the on-disk file directly, bypassing the publisher — the digest recorded
	// at publish time no longer matches what's actually on disk.
	tamperedPath := filepath.Join("specs/active", publishedFiles[0])
	if err := os.WriteFile(tamperedPath, []byte("tampered content"), 0644); err != nil {
		t.Fatalf("failed to tamper: %v", err)
	}

	digests := map[string]string{}
	for name, content := range rendered {
		digests[name] = digestText(content)
	}

	ok, errMsg = verifyPostcondition(digests)
	if ok {
		t.Fatal("expected verifyPostcondition to fail against a tampered file")
	}
	if !strings.Contains(errMsg, "does not match the digest recorded at publish time") {
		t.Fatalf("expected a digest-mismatch message, got %q", errMsg)
	}
}

func TestVerifyPostcondition_MissingFile_ReportsFileList(t *testing.T) {
	chdirTemp(t)

	rendered := samplePublishBundle()
	ok, errMsg := publishDocuments(rendered)
	if !ok {
		t.Fatalf("setup publish failed: %s", errMsg)
	}

	if err := os.Remove(filepath.Join("specs/active", publishedFiles[0])); err != nil {
		t.Fatalf("failed to remove published file: %v", err)
	}

	digests := map[string]string{}
	for name, content := range rendered {
		digests[name] = digestText(content)
	}

	ok, errMsg = verifyPostcondition(digests)
	if ok {
		t.Fatal("expected verifyPostcondition to fail when a published file is missing")
	}
	if !strings.Contains(errMsg, "postcondition failed") {
		t.Fatalf("expected a postcondition failure message, got %q", errMsg)
	}
}
