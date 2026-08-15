package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// The specification flow persists its own state instead of using the Development
// artifact store. This keeps a specification run alive when Development resets its
// own artifacts during a handoff.
const dir = ".harness/specification/active"

// Mirrors SpecificationTasks.Prompt.cs (.NET) — same paths, same JSON shapes, same
// per-phase content (accepted-parent digest, evaluator rules, skill). This file used to
// return a bare one-line phase name and never called engine.Format at all (unlike
// flowsdevelopment/prompts.go, which always does): no <input>/<response> envelope, no
// digest, no shape, no embedded skill. A driver following the shared skill files literally
// — which say "set ideaDigest to the accepted idea digest the prompt gives you" — had
// nothing to copy, and was left reverse-engineering the digest by hashing files and reading
// the compiled binary's strings by hand. That's the direct cause of a real
// PRD_IDEA_DIGEST_STALE failure observed in production.
const (
	ideaProposalPath     = ".harness/specification/active/idea.proposal.json"
	prdProposalPath      = ".harness/specification/active/prd.proposal.json"
	srsProposalPath      = ".harness/specification/active/srs.proposal.json"
	sddProposalPath      = ".harness/specification/active/sdd.proposal.json"
	reviewProposalPath   = ".harness/specification/active/review.proposal.json"
	approvalProposalPath = ".harness/specification/active/approval.proposal.json"

	ideaShape     = `{"schema":"iao/idea/v1","title":"...","problem":"...","users":["..."],"desiredOutcomes":["..."],"constraints":["..."],"openQuestions":[{"id":"OQ-1","question":"...","blocking":false}]}`
	prdShape      = `{"schema":"iao/prd/v1","ideaDigest":"sha256:...","vision":"...","goals":[{"id":"G-1","statement":"..."}],"successMetrics":[{"id":"M-1","goalId":"G-1","measure":"...","target":"..."}],"nonGoals":["..."],"scope":["..."],"risks":[{"id":"R-1","description":"...","mitigation":"...","severity":"..."}],"decisions":[{"id":"D-1","statement":"...","rationale":"..."}],"openQuestions":[]}`
	srsShape      = `{"schema":"iao/srs/v1","prdDigest":"sha256:...","functionalRequirements":[{"id":"RF-1","goalIds":["G-1"],"statement":"...","dependsOn":[],"acceptanceIds":["AC-1"]}],"qualityRequirements":[],"acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"...","when":"...","then":"..."}],"interfaces":[],"dataRules":[],"delivery":{"target":"...","verificationStrategy":"...","isBootstrap":true}}`
	sddShape      = `{"schema":"iao/sdd/v1","srsDigest":"sha256:...","adrs":[{"id":"ADR-1","title":"...","decision":"...","rationale":"...","requirementIds":["RF-1"]}],"controls":[{"id":"IC-1","name":"...","description":"...","requirementIds":["RF-1"]}]}`
	reviewShape   = `{"verdict":"READY","slices":[{"id":"SL-1","classification":"...","goal":"...","inScope":["..."],"outOfScope":["..."],"observableOutcome":"...","requirementIds":["RF-1"],"adrIds":["ADR-1"],"dependsOn":[],"contracts":["..."],"happyPath":"...","failurePath":"...","acceptanceCriterion":"...","suggestedTarget":"...","suggestedVerificationStrategy":"..."}],"conflicts":[],"residuals":[]}`
	approvalShape = `{"decision":"approved","bundleDigest":"sha256:...","rationale":"...","approvedBy":"...","decidedAt":"2026-01-01T00:00:00Z"}`
)

type run struct {
	Step           int            `json:"step"`
	Status         string         `json:"status"`
	Phase          string         `json:"phase"`
	Counters       map[string]int `json:"counters"`
	TraceLabel     *string        `json:"traceLabel"`
	TerminalReason *string        `json:"terminalReason"`
}

func canon(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}

func dig(v any) string {
	h := sha256.Sum256(canon(v))
	return "sha256:" + hex.EncodeToString(h[:])
}

func digestText(value string) string {
	h := sha256.Sum256([]byte(value))
	return "sha256:" + hex.EncodeToString(h[:])
}

func path(n string) string { return filepath.Join(dir, n) }
func readJSON(n string, v any) bool {
	b, e := os.ReadFile(path(n))
	return e == nil && json.Unmarshal(b, v) == nil
}
func writeJSON(n string, v any) {
	_ = os.MkdirAll(dir, 0755)
	_ = os.WriteFile(path(n), canon(v), 0644)
}
func loadRun() run {
	var r run
	if readJSON("run.json", &r) {
		return r
	}
	return run{Status: "in_progress", Phase: "start", Counters: map[string]int{}}
}
func saveRun(r run) { writeJSON("run.json", r) }
func accepted(phase string, v any) (string, bool) {
	n := phase + ".accepted.json"
	b, e := os.ReadFile(path(n))
	if e != nil {
		return "", false
	}
	if json.Unmarshal(b, v) != nil {
		return "", false
	}
	var raw any
	json.Unmarshal(b, &raw)
	return dig(raw), true
}
func acceptedRaw(phase string) (any, string) {
	b, err := os.ReadFile(path(phase + ".accepted.json"))
	if err != nil {
		return nil, ""
	}
	var value any
	if json.Unmarshal(b, &value) != nil {
		return nil, ""
	}
	return value, dig(value)
}
func proposal(phase string, v any) bool {
	return readJSON(phase+".proposal.json", v)
}

func writeAccepted(phase string, value any) string {
	writeJSON(phase+".accepted.json", value)
	return dig(value)
}

type OQ struct {
	Id       string `json:"id"`
	Question string `json:"question"`
	Blocking bool   `json:"blocking"`
}
type Idea struct {
	Schema          string   `json:"schema"`
	Title           string   `json:"title"`
	Problem         string   `json:"problem"`
	Users           []string `json:"users"`
	DesiredOutcomes []string `json:"desiredOutcomes"`
	Constraints     []string `json:"constraints"`
	OpenQuestions   []OQ     `json:"openQuestions"`
}
type Goal struct {
	Id        string `json:"id"`
	Statement string `json:"statement"`
}
type Metric struct {
	Id      string `json:"id"`
	GoalId  string `json:"goalId"`
	Measure string `json:"measure"`
	Target  string `json:"target"`
}
type Risk struct {
	Id          string `json:"id"`
	Description string `json:"description"`
	Mitigation  string `json:"mitigation"`
	Severity    string `json:"severity"`
}
type Decision struct {
	Id        string `json:"id"`
	Statement string `json:"statement"`
	Rationale string `json:"rationale"`
}
type PRD struct {
	Schema         string     `json:"schema"`
	IdeaDigest     string     `json:"ideaDigest"`
	Vision         string     `json:"vision"`
	Goals          []Goal     `json:"goals"`
	SuccessMetrics []Metric   `json:"successMetrics"`
	NonGoals       []string   `json:"nonGoals"`
	Scope          []string   `json:"scope"`
	Risks          []Risk     `json:"risks"`
	Decisions      []Decision `json:"decisions"`
	OpenQuestions  []OQ       `json:"openQuestions"`
}
type Req struct {
	Id            string   `json:"id"`
	GoalIds       []string `json:"goalIds"`
	Statement     string   `json:"statement"`
	DependsOn     []string `json:"dependsOn"`
	AcceptanceIds []string `json:"acceptanceIds"`
}
type AC struct {
	Id             string   `json:"id"`
	RequirementIds []string `json:"requirementIds"`
	Given          string   `json:"given"`
	When           string   `json:"when"`
	Then           string   `json:"then"`
}
type Iface struct {
	Id             string   `json:"id"`
	RequirementIds []string `json:"requirementIds"`
	Name           string   `json:"name"`
	Description    string   `json:"description"`
}
type Rule struct {
	Id             string   `json:"id"`
	RequirementIds []string `json:"requirementIds"`
	Rule           string   `json:"rule"`
}
type Delivery struct {
	Target               string `json:"target"`
	VerificationStrategy string `json:"verificationStrategy"`
	IsBootstrap          bool   `json:"isBootstrap"`
}
type SRS struct {
	Schema                 string   `json:"schema"`
	PrdDigest              string   `json:"prdDigest"`
	FunctionalRequirements []Req    `json:"functionalRequirements"`
	QualityRequirements    []Req    `json:"qualityRequirements"`
	AcceptanceCriteria     []AC     `json:"acceptanceCriteria"`
	Interfaces             []Iface  `json:"interfaces"`
	DataRules              []Rule   `json:"dataRules"`
	Delivery               Delivery `json:"delivery"`
}
type ADR struct {
	Id             string   `json:"id"`
	Title          string   `json:"title"`
	Decision       string   `json:"decision"`
	Rationale      string   `json:"rationale"`
	RequirementIds []string `json:"requirementIds"`
}
type Control struct {
	Id             string   `json:"id"`
	Name           string   `json:"name"`
	Description    string   `json:"description"`
	RequirementIds []string `json:"requirementIds"`
}
type SDD struct {
	Schema    string    `json:"schema"`
	SrsDigest string    `json:"srsDigest"`
	Adrs      []ADR     `json:"adrs"`
	Controls  []Control `json:"controls"`
}
type Slice struct {
	Id                            string   `json:"id"`
	Classification                string   `json:"classification"`
	Goal                          string   `json:"goal"`
	InScope                       []string `json:"inScope"`
	OutOfScope                    []string `json:"outOfScope"`
	ObservableOutcome             string   `json:"observableOutcome"`
	RequirementIds                []string `json:"requirementIds"`
	AdrIds                        []string `json:"adrIds"`
	DependsOn                     []string `json:"dependsOn"`
	Contracts                     []string `json:"contracts"`
	HappyPath                     string   `json:"happyPath"`
	FailurePath                   string   `json:"failurePath"`
	AcceptanceCriterion           string   `json:"acceptanceCriterion"`
	SuggestedTarget               string   `json:"suggestedTarget"`
	SuggestedVerificationStrategy string   `json:"suggestedVerificationStrategy"`
}
type Verdict struct {
	Verdict   string   `json:"verdict"`
	Slices    []Slice  `json:"slices"`
	Conflicts []string `json:"conflicts"`
	Residuals []string `json:"residuals"`
}
type Approval struct {
	Decision     string `json:"decision"`
	BundleDigest string `json:"bundleDigest"`
	Rationale    string `json:"rationale"`
	ApprovedBy   string `json:"approvedBy"`
	DecidedAt    string `json:"decidedAt"`
}

func violationsList(violations []string) string {
	lines := make([]string, len(violations))
	for i, v := range violations {
		lines[i] = "- " + v
	}
	return strings.Join(lines, "\n")
}

// sourcesBlock re-attaches the ingested specs/sources content (written once at `start`, see
// engine.HasDocs/ReadDocs above) on every discover turn, including retries — a driver that
// already dropped a large source dump from its context must not fall back to inventing an
// unrelated idea just because this is a retry, not the first turn.
func sourcesBlock() string {
	bundle, _ := acceptedRaw("sources")
	m, ok := bundle.(map[string]any)
	if !ok {
		return "No sources folder was found (or it was empty). Ask the human operator for the\n" +
			"idea, problem, users and constraints in this conversation, then frame it below.\n"
	}
	filesAny, _ := m["files"].([]any)
	files := make([]string, 0, len(filesAny))
	for _, f := range filesAny {
		if s, ok := f.(string); ok {
			files = append(files, s)
		}
	}
	content, _ := m["content"].(string)
	return fmt.Sprintf("<sources folder=\"specs/sources\" files=\"%s\">%s</sources>\n", strings.Join(files, ", "), engine.Inline(content))
}

// bundleDigest is the same computation approve() validates against — factored out so the
// approve prompt can show the driver the exact value it must echo back.
func bundleDigest() string {
	var i Idea
	var p PRD
	var s SRS
	var d SDD
	var v Verdict
	di, _ := accepted("idea", &i)
	dp, _ := accepted("prd", &p)
	ds, _ := accepted("srs", &s)
	dd, _ := accepted("sdd", &d)
	dr, _ := accepted("readiness", &v)
	return digestText(strings.Join([]string{di, dp, ds, dd, dr}, "|"))
}

func bundlePreview() string {
	var p PRD
	var s SRS
	var d SDD
	var v Verdict
	accepted("prd", &p)
	accepted("srs", &s)
	accepted("sdd", &d)
	accepted("readiness", &v)
	vision := p.Vision
	if strings.TrimSpace(vision) == "" {
		vision = "(missing)"
	}
	verdict := v.Verdict
	if strings.TrimSpace(verdict) == "" {
		verdict = "(missing)"
	}
	return fmt.Sprintf(`## Preview — bundle to be published to specs/active/

- **PRD vision:** %s — %d goal(s), %d success metric(s)
- **SRS:** %d functional + %d quality requirement(s), %d acceptance criteria
- **SDD:** %d ADR(s), %d control(s)
- **Readiness:** verdict '%s', %d slice(s)`,
		vision, len(p.Goals), len(p.SuccessMetrics),
		len(s.FunctionalRequirements), len(s.QualityRequirements), len(s.AcceptanceCriteria),
		len(d.Adrs), len(d.Controls), verdict, len(v.Slices))
}

func discoverPrompt(violations []string) string {
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Frame the idea for this Specification run (blueprint 0004 §2/§3, discover phase).

%sGround the idea in the material above when present — do not invent
facts it doesn't support, and prefer citing/summarizing it over guessing. Write a
JSON OBJECT to the file '%s' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: %s
`+"`schema`"+` must be exactly "iao/idea/v1". Provide a title, a
problem statement, at least one user and one desired outcome; keep the canonical JSON
under 20000 UTF-8 bytes and give every open question a unique id.

Return `+"`discover`"+` without arguments when done; the harness will validate the file and
either advance to `+"`product`"+` or re-request `+"`discover`"+` with the reported violations.`,
			sourcesBlock(), ideaProposalPath, ideaShape)
	} else {
		input = fmt.Sprintf(`%sThe idea proposal at '%s' did not pass IdeaEvaluator:
%s

Rewrite the file at the exact same path with this shape: %s
`+"`schema`"+` must be exactly "iao/idea/v1". Return `+"`discover`"+`
without arguments for another harness-controlled attempt.`,
			sourcesBlock(), ideaProposalPath, violationsList(violations), ideaShape)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "discover", nil), engine.Skills("spec-discovery"))
}

func productPrompt(violations []string) string {
	var i Idea
	ideaDigest, _ := accepted("idea", &i)
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Draft the PRD for this Specification run (blueprint 0004 §2/§3, product phase),
building on the accepted idea (digest '%s').

Write a JSON OBJECT to the file '%s' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: %s
`+"`schema`"+` must be exactly "iao/prd/v1" and `+"`ideaDigest`"+` must be
set to exactly '%s'. Provide at least one goal, one non-goal, one scope
entry and one success metric with a measure and target that are both present and
distinct; leave no blocking open question unresolved.

Return `+"`product`"+` without arguments when done; the harness will validate the file and
either advance to `+"`analysis`"+`, or re-request `+"`product`"+` with the reported violations.`,
			ideaDigest, prdProposalPath, prdShape, ideaDigest)
	} else {
		input = fmt.Sprintf(`The PRD proposal at '%s' did not pass PrdEvaluator:
%s

Rewrite the file at the exact same path with this shape: %s
`+"`schema`"+` must be exactly "iao/prd/v1" and `+"`ideaDigest`"+` must be
set to exactly '%s'. Return `+"`product`"+` without arguments for another
harness-controlled attempt.`,
			prdProposalPath, violationsList(violations), prdShape, ideaDigest)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "product", nil), engine.Skills("spec-product"))
}

func analysisPrompt(violations []string) string {
	var p PRD
	prdDigest, _ := accepted("prd", &p)
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Draft the SRS for this Specification run (blueprint 0004 §2/§3, analysis phase),
building on the accepted PRD (digest '%s').

Write a JSON OBJECT to the file '%s' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: %s
`+"`schema`"+` must be exactly "iao/srs/v1" and `+"`prdDigest`"+` must be
set to exactly '%s'. Every goal from the accepted PRD must be covered by at
least one requirement's `+"`goalIds`"+`. Every functional and quality requirement needs a
unique id and at least one `+"`acceptanceIds`"+` entry that resolves to a real entry in
`+"`acceptanceCriteria`"+`. Every `+"`dependsOn`"+` id and every acceptance criterion/interface/
data-rule requirement reference must point at a requirement id that actually exists.

Return `+"`analysis`"+` without arguments when done; the harness will validate the file and
either advance to `+"`design`"+`, or re-request `+"`analysis`"+` with the reported violations.`,
			prdDigest, srsProposalPath, srsShape, prdDigest)
	} else {
		input = fmt.Sprintf(`The SRS proposal at '%s' did not pass SrsEvaluator:
%s

Rewrite the file at the exact same path with this shape: %s
`+"`schema`"+` must be exactly "iao/srs/v1" and `+"`prdDigest`"+` must be
set to exactly '%s'. Return `+"`analysis`"+` without arguments for another
harness-controlled attempt.`,
			srsProposalPath, violationsList(violations), srsShape, prdDigest)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "analysis", nil), engine.Skills("spec-analysis"))
}

func designPrompt(violations []string) string {
	var s SRS
	srsDigest, _ := accepted("srs", &s)
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Draft the SDD for this Specification run (blueprint 0004 §2/§3, design phase),
building on the accepted SRS (digest '%s').

Write a JSON OBJECT to the file '%s' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: %s
`+"`schema`"+` must be exactly "iao/sdd/v1" and `+"`srsDigest`"+` must be
set to exactly '%s'. Every functional and quality requirement from the
accepted SRS must be allocated to (referenced by) at least one ADR's
`+"`requirementIds`"+`. Every ADR id must be unique, and every ADR/control requirement
reference must point at a requirement id that actually exists in the accepted SRS.

Return `+"`design`"+` without arguments when done; the harness will validate the file and
either persist sdd.accepted.json and stop, or re-request `+"`design`"+` with the reported
violations.`,
			srsDigest, sddProposalPath, sddShape, srsDigest)
	} else {
		input = fmt.Sprintf(`The SDD proposal at '%s' did not pass SddEvaluator:
%s

Rewrite the file at the exact same path with this shape: %s
`+"`schema`"+` must be exactly "iao/sdd/v1" and `+"`srsDigest`"+` must be
set to exactly '%s'. Return `+"`design`"+` without arguments for another
harness-controlled attempt.`,
			sddProposalPath, violationsList(violations), sddShape, srsDigest)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "design", nil), engine.Skills("spec-design"))
}

func reviewPrompt(violations []string) string {
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Assess readiness for this Specification run (blueprint 0004 §2/§7, blueprint 0006
review phase): judge whether the accepted idea/PRD/SRS/SDD chain is internally
consistent, or whether an earlier phase needs to be redone.

Write a JSON OBJECT to the file '%s' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: %s
`+"`verdict`"+` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
"FAIL:design". `+"`conflicts`"+` and `+"`residuals`"+` are always required arrays (use `+"`[]`"+` when
there are none — never omit them).

If `+"`verdict`"+` is "READY": propose at most 10 readiness slices, each with a unique id.
Every `+"`requirementIds`"+` entry must reference a real requirement from the accepted SRS
and every `+"`adrIds`"+` entry must reference a real ADR from the accepted SDD. Every
`+"`dependsOn`"+` entry must name another slice in this same list (never itself, never an
id outside this proposal); the dependency graph must be acyclic, and at least one
slice must have an empty `+"`dependsOn`"+` (a starting slice). Every requirement in the
accepted SRS must be covered by at least one slice's `+"`requirementIds`"+` — the slices
must form a complete cover.

If `+"`verdict`"+` starts with "FAIL:": leave `+"`slices`"+` empty — a FAIL verdict is a
rejection of an earlier phase, not a slice proposal; explain the rejection through
`+"`conflicts`"+`/`+"`residuals`"+` instead.

Return `+"`review`"+` without arguments when done; the harness will validate the file and
either pause for approval (READY), recascade to the failing phase (FAIL:*), or
re-request `+"`review`"+` with the reported violations.`,
			reviewProposalPath, reviewShape)
	} else {
		input = fmt.Sprintf(`The readiness verdict proposal at '%s' did not pass
ReadinessEvaluator:
%s

Rewrite the file at the exact same path with this shape: %s
`+"`verdict`"+` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
"FAIL:design", and `+"`conflicts`"+`/`+"`residuals`"+` must always be present arrays. Return
`+"`review`"+` without arguments for another harness-controlled attempt.`,
			reviewProposalPath, violationsList(violations), reviewShape)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "review", nil), engine.Skills("spec-review"))
}

func approvePrompt(violations []string) string {
	digest := bundleDigest()
	var input string
	if len(violations) == 0 {
		input = fmt.Sprintf(`Review the bundle for this Specification run before publication (blueprint 0004 §5
ApprovalEvaluator, §6 Publicação segura).

%s

The current bundle digest is '%s'.

Write a JSON OBJECT to the file '%s' (a real file, written with
your file-write tool — NOT escaped or embedded inside the envelope you send back)
with this shape: %s
`+"`decision`"+` must be exactly "approved" or "revise". `+"`bundleDigest`"+` must be set to
exactly '%s' — it is re-checked against the CURRENT accepted chain at
evaluation time, so if any accepted document changes after this preview, resend with
the freshly reported digest instead of the one shown here. `+"`rationale`"+` must state a
real reason, not a placeholder.

If `+"`decision`"+` is "approved": the four accepted documents (PRD, SRS, SDD, readiness)
are rendered and published to 'specs/active/' as 00-prd.md,
10-software-requirements-specification.md, 20-software-design-document.md and
30-readiness-handoff.md, and the run completes.

If `+"`decision`"+` is "revise": the run routes back to the review phase so a fresh
readiness verdict — including a FAIL:* one, if a deeper phase needs rework — can be
issued.

Return `+"`approve`"+` without arguments when done; the harness will validate the file and
act on the decision, or re-request `+"`approve`"+` with the reported violations.`,
			bundlePreview(), digest, approvalProposalPath, approvalShape, digest)
	} else {
		input = fmt.Sprintf(`The approval proposal at '%s' did not pass ApprovalEvaluator:
%s

The current bundle digest is '%s'. Rewrite the file at the exact same
path with this shape: %s
`+"`decision`"+` must be exactly "approved" or "revise", `+"`bundleDigest`"+` must be set to
exactly '%s', and `+"`rationale`"+` must state a real reason. Return `+"`approve`"+`
without arguments for another harness-controlled attempt.`,
			approvalProposalPath, violationsList(violations), digest, approvalShape, digest)
	}
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "approve", nil), engine.Skills("spec-review"))
}

func prompt(p string, errs ...string) string {
	switch p {
	case "discover":
		return discoverPrompt(errs)
	case "product":
		return productPrompt(errs)
	case "analysis":
		return analysisPrompt(errs)
	case "design":
		return designPrompt(errs)
	case "review":
		return reviewPrompt(errs)
	case "approve":
		return approvePrompt(errs)
	default:
		r := "Specification phase: " + p
		if len(errs) > 0 {
			r += " Violations: " + strings.Join(errs, "; ")
		}
		return r
	}
}
func start() string {
	r := loadRun()
	resumablePhase := r.Phase == "discover" || r.Phase == "product" ||
		r.Phase == "analysis" || r.Phase == "design" || r.Phase == "review"
	resumable := r.Status == "in_progress" && resumablePhase ||
		r.Status == "awaiting_approval" && r.Phase == "approve"
	if resumable {
		return prompt(r.Phase)
	}
	os.RemoveAll(dir)
	r = run{Status: "in_progress", Phase: "discover", Counters: map[string]int{}}
	saveRun(r)
	if engine.HasDocs("specs/sources") {
		content, files := engine.ReadDocs("specs/sources")
		writeJSON("sources.accepted.json", map[string]any{"files": files, "content": content})
	}
	return prompt("discover")
}
func advance(p string) string {
	r := loadRun()
	r.Phase = p
	r.Status = "in_progress"
	r.Step = engine.LoadState().Step
	saveRun(r)
	return prompt(p)
}

// Each task follows the same shape: load the proposal, validate the phase-specific
// parent digest, persist the accepted document, and route to the next phase.
func discover(*engine.Envelope) string {
	var x Idea
	if !proposal("idea", &x) {
		return prompt("discover", fmt.Sprintf("no readable idea proposal was found at '%s' (missing or not valid JSON).", ideaProposalPath))
	}
	source := "driver"
	sourceDigest := dig(x)
	if bundle, digestValue := acceptedRaw("sources"); bundle != nil {
		source = "specs/sources"
		sourceDigest = digestValue
	}
	if violations := validateIdea(x, source, sourceDigest); len(violations) > 0 {
		return prompt("discover", formatViolations(violations)...)
	}
	writeAccepted("idea", x)
	return advance("product")
}
func product(*engine.Envelope) string {
	var x PRD
	if !proposal("prd", &x) {
		return prompt("product", fmt.Sprintf("no readable PRD proposal was found at '%s' (missing or not valid JSON).", prdProposalPath))
	}
	// Deliberately does not short-circuit on a missing accepted idea: an absent parent
	// contributes an empty digest, and validatePrd's own PRD_IDEA_DIGEST_STALE check
	// reports it through the same evaluator pass as every other violation — mirrors
	// SpecificationTasks.Product (.NET), which passes `ideaDigest ?? ""` rather than
	// special-casing the missing-parent case ahead of the evaluator.
	var i Idea
	id, _ := accepted("idea", &i)
	if violations := validatePrd(x, id); len(violations) > 0 {
		return prompt("product", formatViolations(violations)...)
	}
	writeAccepted("prd", x)
	return advance("analysis")
}
func analysis(*engine.Envelope) string {
	var x SRS
	if !proposal("srs", &x) {
		return prompt("analysis", fmt.Sprintf("no readable SRS proposal was found at '%s' (missing or not valid JSON).", srsProposalPath))
	}
	// See product()'s comment: a missing accepted PRD is not short-circuited, it flows
	// into validateSrs as an empty digest and surfaces as SRS_PRD_DIGEST_STALE.
	var p PRD
	pd, _ := accepted("prd", &p)
	goals := make([]string, 0, len(p.Goals))
	for _, goal := range p.Goals {
		goals = append(goals, goal.Id)
	}
	if violations := validateSrs(x, pd, goals); len(violations) > 0 {
		return prompt("analysis", formatViolations(violations)...)
	}
	writeAccepted("srs", x)
	return advance("design")
}
func design(*engine.Envelope) string {
	var x SDD
	if !proposal("sdd", &x) {
		return prompt("design", fmt.Sprintf("no readable SDD proposal was found at '%s' (missing or not valid JSON).", sddProposalPath))
	}
	// See product()'s comment: a missing accepted SRS is not short-circuited, it flows
	// into validateSdd as an empty digest and surfaces as SDD_SRS_DIGEST_STALE.
	var s SRS
	sd, _ := accepted("srs", &s)
	requirements := append(append([]Req{}, s.FunctionalRequirements...), s.QualityRequirements...)
	if violations := validateSdd(x, sd, reqIDs(requirements)); len(violations) > 0 {
		return prompt("design", formatViolations(violations)...)
	}
	writeAccepted("sdd", x)
	return advance("review")
}
func review(*engine.Envelope) string {
	var x Verdict
	var s SRS
	var d SDD
	if !proposal("review", &x) {
		return prompt("review", fmt.Sprintf("no readable readiness verdict proposal was found at '%s' (missing or not valid JSON).", reviewProposalPath))
	}
	accepted("srs", &s)
	accepted("sdd", &d)
	requirements := reqIDs(append(append([]Req{}, s.FunctionalRequirements...), s.QualityRequirements...))
	if violations := validateReadiness(x, requirements, adrIDs(d.Adrs)); len(violations) > 0 {
		return prompt("review", formatViolations(violations)...)
	}
	r := loadRun()
	if x.Verdict == "READY" {
		writeAccepted("readiness", x)
		r.Status = "awaiting_approval"
		r.Phase = "approve"
		saveRun(r)
		// Pauses the loop for real human approval (mirrors SpecificationTasks.Review
		// (.NET), which returns the literal "stop" sentinel here, not another prompt) —
		// the harness (TaskRegistry) recognizes "stop" as TraceOutcome.Stop and halts
		// automatic dispatch until a fresh `start` resumes at the persisted "approve"
		// phase.
		return "stop"
	}
	r.Counters["recascades"]++
	if r.Counters["recascades"] > 2 {
		reason := "recascade limit reached"
		r.Status = "needs_human_decision"
		r.Phase = "stop"
		r.TerminalReason = &reason
		saveRun(r)
		return "stop"
	}
	r.Phase = strings.SplitN(x.Verdict, ":", 2)[1]
	saveRun(r)
	return prompt(r.Phase)
}
func approve(*engine.Envelope) string {
	var x Approval
	if !proposal("approval", &x) {
		return prompt("approve", fmt.Sprintf("no readable approval proposal was found at '%s' (missing or not valid JSON).", approvalProposalPath))
	}
	var p PRD
	var s SRS
	var d SDD
	var v Verdict
	_, prdOk := accepted("prd", &p)
	_, srsOk := accepted("srs", &s)
	_, sddOk := accepted("sdd", &d)
	_, readinessOk := accepted("readiness", &v)
	cur := bundleDigest()
	if violations := validateApproval(x, cur); len(violations) > 0 {
		return prompt("approve", formatViolations(violations)...)
	}
	r := loadRun()
	if x.Decision == "revise" {
		r.Status = "in_progress"
		r.Phase = "review"
		saveRun(r)
		return prompt("review")
	}

	// decision == "approved" (validateApproval already restricted the allowed set).
	// Reads the four accepted documents that make up the publishable bundle — idea is
	// not part of the published bundle. Mirrors SpecificationTasks.Approve (.NET): a
	// missing accepted document blocks publish outright instead of silently rendering
	// an empty/incomplete bundle.
	if !prdOk || !srsOk || !sddOk || !readinessOk {
		reason := "one or more accepted documents (prd/srs/sdd/readiness) are missing; cannot publish."
		r.Status = "publish_blocked"
		r.TerminalReason = &reason
		saveRun(r)
		return "stop"
	}

	// Pre-publish gate (blueprint 0006 "Antes de promover, o DevelopmentReadinessEvaluator
	// deve provar..."): rendered via the SAME renderBundle a real publishDocuments call
	// uses, so this byte-budget/readiness check sees exactly what would be written —
	// never a duplicated/divergent render. specs/active/ is not touched at all if this
	// fails.
	rendered := renderBundle(p, s, d, v)
	if violations := developmentReady(p, s, d, v, x.BundleDigest, cur, rendered, engine.CurrentConfig().DocsMaxChars); len(violations) > 0 {
		reason := strings.Join(formatViolations(violations), "; ")
		r.Status = "publish_blocked"
		r.TerminalReason = &reason
		saveRun(r)
		return "stop"
	}

	if ok, errMsg := publishDocuments(rendered); !ok {
		r.Status = "publish_blocked"
		r.TerminalReason = &errMsg
		saveRun(r)
		return "stop"
	}
	r.Status = "completed"
	r.Phase = "stop"
	saveRun(r)
	return "stop"
}
func main() {
	tasks := map[string]engine.Action{
		"start":    func(*engine.Envelope) string { return start() },
		"discover": discover,
		"product":  product,
		"analysis": analysis,
		"design":   design,
		"review":   review,
		"approve":  approve,
	}
	max := 18
	engine.Run(os.Args[1:], tasks, engine.RunOptions{
		TraceSnapshotPath: ".harness/last-specification.trace.jsonl",
		StateSnapshotPath: ".harness/last-specification.state.json",
		MaxSteps:          &max,
		// Matches SpecificationTasks/Program.cs (.NET) exactly: Status != "in_progress",
		// with no carve-out for "awaiting_approval". A run paused at "approve" is resumed
		// by start()'s own resumable check (status=="awaiting_approval" && phase=="approve"
		// below) — ShouldResetOnStart only decides whether the underlying trace/state
		// snapshot gets truncated, and the .NET reference never special-cases that status
		// here.
		ShouldResetOnStart: func() bool {
			return loadRun().Status != "in_progress"
		},
	})
}
