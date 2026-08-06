package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// id 1 has priority 2; id 2 has priority 1 → the highest priority is id 2.
const featuresJSON = `[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]`

func isolate(t *testing.T) (targetDir, specsDir string) {
	t.Helper()

	dir := t.TempDir()
	previous, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	if err := os.Chdir(dir); err != nil {
		t.Fatalf("chdir: %v", err)
	}
	t.Cleanup(func() {
		_ = os.Chdir(previous)
		engine.ReloadConfig()
	})
	engine.ReloadConfig()

	targetDir = filepath.Join(t.TempDir(), "target")
	if err := os.MkdirAll(targetDir, 0o755); err != nil {
		t.Fatal(err)
	}
	specsDir = filepath.Join(dir, "specs")

	return targetDir, specsDir
}

func givenSpecsBrief(t *testing.T, specsDir, content string) {
	t.Helper()
	if err := os.MkdirAll(specsDir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(specsDir, "brief.md"), []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func cmd(value string, args ...string) *engine.Envelope {
	e := engine.NewEnvelope(engine.EnvelopeType.Command, value, args)
	return &e
}

// writePlanFile writes the driver-side feature array to planFilePath — Plan() reads
// features from that file, not from the envelope's args (see planFilePath in tasks.go).
func writePlanFile(t *testing.T, features string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(planFilePath), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(planFilePath, []byte(features), 0o644); err != nil {
		t.Fatal(err)
	}
}

func planCmd(t *testing.T, features string, verifyCmd, targetDir string) *engine.Envelope {
	t.Helper()
	writePlanFile(t, features)
	return cmd("plan", verifyCmd, targetDir)
}

func planWith(targetDir string) string {
	if err := os.WriteFile(filepath.Join(targetDir, "init.sh"), []byte("#!/usr/bin/env bash\nset -e\n"), 0o755); err != nil {
		panic(err)
	}
	if err := os.MkdirAll(filepath.Dir(planFilePath), 0o755); err != nil {
		panic(err)
	}
	if err := os.WriteFile(planFilePath, []byte(featuresJSON), 0o644); err != nil {
		panic(err)
	}
	result := Plan(cmd("plan", "dotnet test", targetDir))
	return result
}

// advanceToVerify drives the flow up to an implemented, not-yet-verified feature.
func advanceToVerify(targetDir string) {
	planWith(targetDir)
	Implement(cmd("implement", "implemented"))
}

func writeVerifyFeatureScript(t *testing.T, targetDir, body string) {
	t.Helper()
	if err := os.MkdirAll(targetDir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(targetDir, "verify-feature.sh"), []byte(body), 0o755); err != nil {
		t.Fatal(err)
	}
}

func verifyLogPath(featureId int) string {
	return filepath.Join(".harness", "logs", fmt.Sprintf("verify-feature-%d.log", featureId))
}

func readFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading %s: %v", path, err)
	}
	return string(data)
}

func gitRun(t *testing.T, dir string, args ...string) string {
	t.Helper()
	result := engine.RunGitCommand(dir, args...)
	if result.ExitCode != 0 {
		t.Fatalf("git %s failed: %s%s", strings.Join(args, " "), result.Error, result.Output)
	}
	return result.Output
}

func TestStart_NoPendingFeature_ResetsFeatureListAndRunConfig(t *testing.T) {
	targetDir, _ := isolate(t)

	planWith(targetDir)
	for _, f := range engine.LoadFeatures() {
		engine.MarkFeaturePassed(f.Id)
	}
	if len(engine.LoadFeatures()) == 0 {
		t.Fatal("expected non-empty features before reset")
	}

	Start()

	if len(engine.LoadFeatures()) != 0 {
		t.Fatal("expected empty feature list")
	}
	if got := engine.LoadRunConfig(); got != engine.DefaultRunConfig() {
		t.Fatalf("unexpected run config: %+v", got)
	}
}

func TestStart_WithPendingFeature_ResumesViaBearingsInsteadOfResetting(t *testing.T) {
	targetDir, _ := isolate(t)

	advanceToVerify(targetDir) // ...→ implement, session "dies" here, before verify

	result := Start()

	if !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("expected implementation prompt, got: %s", result)
	}
	if len(engine.LoadFeatures()) != 2 || engine.PendingFeatureCount() != 2 {
		t.Fatalf("expected features intact and pending")
	}
	if engine.LoadRunConfig().VerifyCmd != "dotnet test" || engine.LoadRunConfig().TargetDir != targetDir {
		t.Fatalf("unexpected run config: %+v", engine.LoadRunConfig())
	}
}

func TestDispatch_StartWithPendingFeature_DoesNotTruncateTraceOrStep(t *testing.T) {
	targetDir, _ := isolate(t)

	advanceToVerify(targetDir)
	engine.AppendTrace(41, "handoff", engine.TraceOutcome.Instruction, 10, "")
	stepBefore := engine.LoadState().Step

	tasks := map[string]engine.Action{
		"start":     func(*engine.Envelope) string { return Start() },
		"plan":      Plan,
		"bearings":  Bearings,
		"smoke":     Smoke,
		"pick":      Pick,
		"implement": Implement,
	}
	shouldReset := func() bool { return engine.PendingFeatureCount() == 0 }
	result := engine.Dispatch([]string{`{"type":"text","value":"start"}`}, tasks, nil, nil, shouldReset)

	if !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("expected resume through deterministic phases, got: %s", result)
	}
	found := false
	for _, e := range engine.LoadTrace() {
		if e.Step == 41 && e.Command == "handoff" {
			found = true
		}
	}
	if !found {
		t.Fatal("expected trace to be preserved")
	}
	if engine.LoadState().Step != stepBefore+1 {
		t.Fatalf("expected step counter to continue, got %d", engine.LoadState().Step)
	}
}

func TestPlan_PersistsFeaturesAndRoutesToBearings(t *testing.T) {
	_, _ = isolate(t)
	if err := os.MkdirAll("web", 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile("web/init.sh", []byte("#!/usr/bin/env bash\nset -e\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	result := Plan(planCmd(t, featuresJSON, "npm test", "web"))

	if len(engine.LoadFeatures()) != 2 {
		t.Fatal("expected two features")
	}
	if engine.LoadRunConfig().VerifyCmd != "npm test" || engine.LoadRunConfig().TargetDir != "web" {
		t.Fatalf("unexpected run config: %+v", engine.LoadRunConfig())
	}
	if !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestPlan_GeneratesNonEmptyRunId(t *testing.T) {
	_, _ = isolate(t)

	Plan(planCmd(t, featuresJSON, "npm test", "web"))

	if runId := engine.LoadRunConfig().RunId; strings.TrimSpace(runId) == "" {
		t.Fatal("expected non-empty run id")
	}
}

func TestStart_WithPendingFeature_PreservesRunIdFromPreviousPlan(t *testing.T) {
	targetDir, _ := isolate(t)

	advanceToVerify(targetDir)
	runIdBefore := engine.LoadRunConfig().RunId
	if strings.TrimSpace(runIdBefore) == "" {
		t.Fatal("expected non-empty run id")
	}

	Start()

	if got := engine.LoadRunConfig().RunId; got != runIdBefore {
		t.Fatalf("expected run id to survive resume: %s vs %s", got, runIdBefore)
	}
}

func TestStart_WithDocs_PersistsBriefInArtifactStore(t *testing.T) {
	_, specsDir := isolate(t)
	givenSpecsBrief(t, specsDir, "# Brief\n\nBuild a task app.")

	Start()

	if !strings.Contains(engine.ReadArtifact("brief"), "Build a task app.") {
		t.Fatalf("unexpected brief: %s", engine.ReadArtifact("brief"))
	}
}

func TestStart_InteractiveMode_DoesNotPersistBrief(t *testing.T) {
	_, _ = isolate(t)

	Start() // no specs/ → InitializerInteractive()

	if engine.ReadArtifact("brief") != "" {
		t.Fatal("expected no brief")
	}
}

func TestStart_NewRunWithoutDocs_ClearsPreviousBrief(t *testing.T) {
	_, specsDir := isolate(t)
	givenSpecsBrief(t, specsDir, "topic A brief")
	Start()
	planWith(".")
	for _, f := range engine.LoadFeatures() {
		engine.MarkFeaturePassed(f.Id)
	}
	os.RemoveAll(specsDir)

	Start() // new run, no specs/ → interactive

	if engine.ReadArtifact("brief") != "" {
		t.Fatal("expected brief cleared")
	}
}

func TestPlan_ReturnsImplementWithoutBriefReinjection(t *testing.T) {
	targetDir, specsDir := isolate(t)
	givenSpecsBrief(t, specsDir, "topic A brief")
	Start()

	result := planWith(targetDir)

	if strings.Contains(result, "topic A brief") {
		t.Fatalf("unexpected brief reinjection: %s", result)
	}
}

func TestPick_ReturnsImplementWithoutBriefReinjection(t *testing.T) {
	targetDir, specsDir := isolate(t)
	givenSpecsBrief(t, specsDir, "topic A brief")
	Start()
	result := planWith(targetDir)

	if strings.Contains(result, "topic A brief") {
		t.Fatalf("unexpected brief reinjection: %s", result)
	}
}

func TestBearingsAndImplement_WithoutPersistedBrief_HaveNoBriefTag(t *testing.T) {
	targetDir, _ := isolate(t)

	bearings := planWith(targetDir)
	implement := bearings

	if strings.Contains(bearings, "<brief>") || strings.Contains(implement, "<brief>") {
		t.Fatalf("unexpected brief tag present")
	}
}

func TestPick_ReturnsImplementWithFeatureDescriptionAndReferences(t *testing.T) {
	targetDir, _ := isolate(t)
	json := `[{"id":1,"title":"A","priority":2,"description":"does X","references":["RF-003"],"implementationContext":{"requirements":["inline X"]}},{"id":2,"title":"B","priority":1}]`
	if err := os.WriteFile(filepath.Join(targetDir, "init.sh"), []byte("#!/usr/bin/env bash\nset -e\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	Plan(planCmd(t, json, "dotnet test", targetDir)) // picks "B" (priority 1)
	writeVerifyFeatureScript(t, targetDir, "#!/usr/bin/env bash\nset -e\n")
	result := Implement(cmd("implement", "done")) // verifies B, hands off, and picks A

	if !strings.Contains(result, "Description: does X") || !strings.Contains(result, "Brief references: RF-003") {
		t.Fatalf("unexpected result: %s", result)
	}
	if !strings.Contains(result, "<implementation-context>requirements: inline X") || strings.Contains(result, "<brief>") {
		t.Fatalf("unexpected inline context: %s", result)
	}
}

func TestPick_WithoutDescriptionOrReferences_HasNoContextBlock(t *testing.T) {
	targetDir, _ := isolate(t)
	result := planWith(targetDir)

	if strings.Contains(result, "Description:") || strings.Contains(result, "Brief references:") {
		t.Fatalf("unexpected context block: %s", result)
	}
}

func TestPlan_InvalidFeatures_ReemitsThePlan(t *testing.T) {
	_, _ = isolate(t)

	result := Plan(planCmd(t, "not json", "dotnet test", "."))

	if len(engine.LoadFeatures()) != 0 {
		t.Fatal("expected no features")
	}
	if got := engine.LoadRunConfig(); got != engine.DefaultRunConfig() {
		t.Fatalf("expected nothing persisted, got %+v", got)
	}
	if !strings.Contains(result, `"value":"plan"`) || strings.Contains(result, "NEW SESSION") {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestPick_ChoosesHighestPriorityAndRecordsCurrentFeature(t *testing.T) {
	targetDir, _ := isolate(t)
	implement := planWith(targetDir)

	if got := engine.GetState(currentFeatureIdKey); got == nil || *got != "2" {
		t.Fatalf("unexpected current feature id: %v", got)
	}
	if !strings.Contains(implement, "B") || !strings.Contains(implement, `"value":"implement"`) {
		t.Fatalf("unexpected result: %s", implement)
	}
	if !strings.Contains(implement, "<input>\n    === NEW SESSION (clean context) ===") {
		t.Fatalf("expected a clean-context boundary before a new feature: %s", implement)
	}
}

func TestVerify_Fail_GoesBackToImplement(t *testing.T) {
	targetDir, _ := isolate(t)
	advanceToVerify(targetDir)

	result := Verify(cmd("verify", "FAIL: testes vermelhos"))

	if !strings.Contains(result, "FAILED") || !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	if strings.Contains(result, "NEW SESSION") {
		t.Fatalf("a retry of the same feature must not request a new context: %s", result)
	}
}

func TestVerify_Pass_RunsAutomatedHandoffAndAdvances(t *testing.T) {
	targetDir, _ := isolate(t)
	advanceToVerify(targetDir)
	writeVerifyFeatureScript(t, targetDir, "#!/usr/bin/env bash\nset -e\n")

	result := Verify(cmd("verify", "PASS"))

	if !strings.Contains(result, `"value":"implement"`) { // id 1 still pending
		t.Fatalf("unexpected result: %s", result)
	}
	if strings.Contains(result, `"value":"handoff"`) {
		t.Fatalf("unexpected handoff prompt: %s", result)
	}
	if engine.PendingFeatureCount() != 1 {
		t.Fatalf("unexpected pending count: %d", engine.PendingFeatureCount())
	}
	if !strings.Contains(readFile(t, filepath.Join(targetDir, "progress.txt")), "Feature #2") {
		t.Fatal("expected progress.txt to mention feature #2")
	}
}

func TestImplement_WithPassingVerifyFeature_RunsVerifyAndHandoffAutomatically(t *testing.T) {
	targetDir, _ := isolate(t)
	writeVerifyFeatureScript(t, targetDir, "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verified\"\n")
	planWith(targetDir)
	result := Implement(cmd("implement", "implemented"))

	if !strings.Contains(result, `"value":"implement"`) || strings.Contains(result, `"value":"verify"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	if engine.PendingFeatureCount() != 1 {
		t.Fatalf("unexpected pending count: %d", engine.PendingFeatureCount())
	}
	progress := readFile(t, filepath.Join(targetDir, "progress.txt"))
	if !strings.Contains(progress, "Feature #2") || !strings.Contains(progress, "PASS: verify-feature.sh 2 passed") {
		t.Fatalf("unexpected progress: %s", progress)
	}
	if !strings.Contains(progress, ".harness/logs/verify-feature-2.log") {
		t.Fatalf("unexpected progress: %s", progress)
	}
	if !strings.Contains(readFile(t, verifyLogPath(2)), "command: bash ./verify-feature.sh 2") {
		t.Fatal("unexpected log content")
	}
}

func TestImplement_WithFailingVerifyFeature_GoesBackToFix(t *testing.T) {
	targetDir, _ := isolate(t)
	writeVerifyFeatureScript(t, targetDir,
		"#!/usr/bin/env bash\nset -euo pipefail\necho \"FAIL: feature $1 broke\"\necho \"DETAILED LINE THAT STAYS ONLY IN THE LOG\"\nexit 7\n")
	planWith(targetDir)
	Bearings(cmd("bearings", "oriented"))
	Smoke(cmd("smoke", "baseline ok"))
	Pick(cmd("pick"))

	result := Implement(cmd("implement", "implemented"))

	if !strings.Contains(result, "FAILED") || !strings.Contains(result, "feature 2 broke") {
		t.Fatalf("unexpected result: %s", result)
	}
	if !strings.Contains(result, ".harness/logs/verify-feature-2.log") {
		t.Fatalf("unexpected result: %s", result)
	}
	if strings.Contains(result, "DETAILED LINE THAT STAYS ONLY IN THE LOG") {
		t.Fatalf("expected detailed line to stay in the log only: %s", result)
	}
	log := readFile(t, verifyLogPath(2))
	if !strings.Contains(log, "FAIL: feature 2 broke") || !strings.Contains(log, "DETAILED LINE THAT STAYS ONLY IN THE LOG") {
		t.Fatalf("unexpected log: %s", log)
	}
	if !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	if engine.PendingFeatureCount() != 2 {
		t.Fatalf("unexpected pending count: %d", engine.PendingFeatureCount())
	}
	if _, err := os.Stat(filepath.Join(targetDir, "progress.txt")); err == nil {
		t.Fatal("expected no progress.txt")
	}
}

func TestVerify_InvalidVerdict_ReemitsVerify(t *testing.T) {
	targetDir, _ := isolate(t)
	advanceToVerify(targetDir)

	result := Verify(cmd("verify", "I ran the tests and it passed"))

	if !strings.Contains(result, `"value":"implement"`) || strings.Contains(result, `"value":"handoff"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	if !strings.Contains(result, "FAILED") {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestHandoff_WithoutDeterministicPass_ReturnsToVerify(t *testing.T) {
	targetDir, _ := isolate(t)
	advanceToVerify(targetDir)

	result := Handoff(cmd("handoff", ""))

	if !strings.Contains(result, `"value":"verify"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	if engine.PendingFeatureCount() != 2 {
		t.Fatalf("unexpected pending count: %d", engine.PendingFeatureCount())
	}
}

func TestHandoff_WithPending_OpensNewSession_AllPassing_Stops(t *testing.T) {
	targetDir, _ := isolate(t)

	advanceToVerify(targetDir)
	writeVerifyFeatureScript(t, targetDir, "#!/usr/bin/env bash\nset -e\n")
	afterFirst := Verify(cmd("verify", "PASS"))

	if !strings.Contains(afterFirst, `"value":"implement"`) || engine.PendingFeatureCount() != 1 {
		t.Fatalf("unexpected state after first feature: %s", afterFirst)
	}

	afterSecond := Implement(cmd("implement", "done"))

	if afterSecond != "stop" || !engine.AllFeaturesPassing() {
		t.Fatalf("unexpected end state: %s", afterSecond)
	}
}

func TestHandoff_TextualHashDoesNotReplaceDeterministicVerify(t *testing.T) {
	targetDir, _ := isolate(t)
	advanceToVerify(targetDir)

	result := Handoff(cmd("handoff", "abc123"))

	if !strings.Contains(result, `"value":"verify"`) || engine.PendingFeatureCount() != 2 {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestVerify_Pass_AutomatedHandoffOnlyCommitsTargetDir(t *testing.T) {
	root, _ := isolate(t)
	repo := filepath.Join(root, "repo")
	target := filepath.Join(repo, "app")
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatal(err)
	}
	gitRun(t, repo, "init")
	gitRun(t, repo, "config", "user.email", "harness@example.test")
	gitRun(t, repo, "config", "user.name", "Harness Test")
	if err := os.WriteFile(filepath.Join(repo, "outside.txt"), []byte("outside the target"), 0o644); err != nil {
		t.Fatal(err)
	}

	if err := os.WriteFile(filepath.Join(target, "init.sh"), []byte("#!/usr/bin/env bash\nset -e\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	Plan(planCmd(t, featuresJSON, "dotnet test", target))
	writeVerifyFeatureScript(t, target, "#!/usr/bin/env bash\nset -e\n")
	result := Implement(cmd("implement", "done in target"))

	if !strings.Contains(result, `"value":"implement"`) {
		t.Fatalf("unexpected result: %s", result)
	}
	committedFiles := gitRun(t, repo, "show", "--name-only", "--format=", "HEAD")
	if !strings.Contains(committedFiles, "app/progress.txt") || strings.Contains(committedFiles, "outside.txt") {
		t.Fatalf("unexpected committed files: %s", committedFiles)
	}
	if !strings.Contains(gitRun(t, repo, "status", "--short"), "?? outside.txt") {
		t.Fatal("expected outside.txt to remain untracked")
	}
}

func TestPerFeatureGuard_ExceedingCeiling_Stops(t *testing.T) {
	targetDir, _ := isolate(t)
	planWith(targetDir)
	Bearings(cmd("bearings", "ok")) // resets to 1
	engine.SetState(featureStepsKey, fmt.Sprintf("%d", StepsPerFeature))

	result := Smoke(cmd("smoke", "ok")) // next bump exceeds

	if result != "stop" {
		t.Fatalf("expected stop, got %s", result)
	}
}

func TestPlan_CyclicDependsOn_ReemitsThePlan(t *testing.T) {
	_, _ = isolate(t)

	result := Plan(planCmd(t,
		`[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]`,
		"dotnet test", "."))

	if len(engine.LoadFeatures()) != 0 {
		t.Fatal("expected empty features")
	}
	if got := engine.LoadRunConfig(); got != engine.DefaultRunConfig() {
		t.Fatalf("unexpected run config: %+v", got)
	}
	if !strings.Contains(result, `"value":"plan"`) || strings.Contains(result, "NEW SESSION") {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestPlan_NonExistentDependsOnId_ReemitsThePlan(t *testing.T) {
	_, _ = isolate(t)

	result := Plan(planCmd(t, `[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]`, "dotnet test", "."))

	if len(engine.LoadFeatures()) != 0 {
		t.Fatal("expected empty features")
	}
	if !strings.Contains(result, `"value":"plan"`) || strings.Contains(result, "NEW SESSION") {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestPlan_CappingMaxFeatures_RemovesDependencyOnCutId(t *testing.T) {
	_, _ = isolate(t)

	// id 1 (priority 1, the best) survives the cut; depends on id 2, whose priority (1000)
	// is the worst of all — guaranteed to be cut by the MaxFeatures cap. The "extras" fill
	// the remaining slots with intermediate priorities.
	var extras strings.Builder
	for i := 3; i < 3+MaxFeatures-1; i++ {
		if extras.Len() > 0 {
			extras.WriteString(",")
		}
		fmt.Fprintf(&extras, `{"id":%d,"title":"extra%d","priority":%d}`, i, i, i)
	}
	json := fmt.Sprintf(`[{"id":1,"title":"survivor","priority":1,"dependsOn":[2]},{"id":2,"title":"cut","priority":1000},%s]`, extras.String())

	Plan(planCmd(t, json, "dotnet test", "."))

	for _, f := range engine.LoadFeatures() {
		if f.Id == 2 {
			t.Fatal("expected id 2 to be cut")
		}
	}
	var survivor *engine.Feature
	for i, f := range engine.LoadFeatures() {
		if f.Id == 1 {
			survivor = &engine.LoadFeatures()[i]
		}
	}
	if survivor == nil {
		t.Fatal("expected survivor feature 1")
	}
	for _, dep := range survivor.DependsOn {
		if dep == 2 {
			t.Fatal("expected dependency on cut id to be removed")
		}
	}
}

func TestPick_RespectsDependency_PicksDependencyBeforeDependent(t *testing.T) {
	_, _ = isolate(t)
	json := `[{"id":1,"title":"foundation","priority":2},{"id":2,"title":"dependent","priority":1,"dependsOn":[1]}]`
	Plan(planCmd(t, json, "dotnet test", "."))
	Bearings(cmd("bearings", "ok"))
	Smoke(cmd("smoke", "ok"))

	Pick(cmd("pick"))

	if got := engine.GetState(currentFeatureIdKey); got == nil || *got != "1" {
		t.Fatalf("unexpected current feature id: %v", got)
	}
}

func TestPick_NoReadyFeatureButPending_StopsWithoutReportingDone(t *testing.T) {
	_, _ = isolate(t)
	planWith(".") // populates RunConfig; the list is overwritten next
	engine.WriteFeatures([]engine.Feature{
		{Id: 1, Title: "A", Priority: 1, DependsOn: []int{2}, References: []string{}},
		{Id: 2, Title: "B", Priority: 2, DependsOn: []int{1}, References: []string{}},
	})
	Bearings(cmd("bearings", "ok"))
	Smoke(cmd("smoke", "ok"))

	result := Pick(cmd("pick"))

	if result != "stop" {
		t.Fatalf("expected stop, got %s", result)
	}
	if engine.PendingFeatureCount() != 2 {
		t.Fatalf("expected nothing marked passed, pending=%d", engine.PendingFeatureCount())
	}
}
