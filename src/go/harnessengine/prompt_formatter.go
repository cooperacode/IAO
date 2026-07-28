package harnessengine

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// Skills builds the {name -> skills/<name>/SKILL.md} map consumed by Format.
func Skills(names ...string) map[string]string {
	skills := make(map[string]string, len(names))
	for _, name := range names {
		if strings.TrimSpace(name) == "" {
			continue
		}
		skills[name] = filepath.Join("skills", name, "SKILL.md")
	}
	return skills
}

// Format assembles the instruction block (input/response/skills) delivered to the model.
func Format(input string, output Envelope, skills map[string]string) string {
	// Reinjects the driver context (captured on `start`, see TaskRegistry/StateStore) into
	// every output — a single point, so no task has to pass it along manually.
	enriched := output
	if enriched.Context == nil {
		enriched.Context = GetContext()
	}

	return fmt.Sprintf(`Execute the instruction inside the `+"`input`"+` tag. Then reply with the result as JSON.

Output contract — a reply that breaks any of these rules is invalid and wastes a retry:
1. Output EXACTLY one JSON object, on a SINGLE line, matching the shape in the `+"`response`"+` tag with the placeholders replaced by real values.
2. The object is the ONLY thing you output: no markdown code fences, no comments, no prose before or after it, nothing.
3. Keep the same keys, types and nesting as the schema — do not add, remove, rename fields, or wrap the object in an array.
4. Every value must be valid JSON: use only double quotes for strings, escape `+"`\"`"+` and `+"`\\`"+` inside them, and replace any line break inside a value with the literal characters `+"`\\n`"+` — never a raw newline. No trailing commas.
5. Before answering, mentally re-parse your own output as JSON; if it would fail to parse, fix it before sending.

%s
<input>
    %s
</input>
<response>
    %s
</response>`, readSkills(skills), input, enriched.ToJSON())
}

func readSkills(skills map[string]string) string {
	if skills == nil {
		return ""
	}

	// Sorted for determinism: Go map iteration order is not stable across runs.
	ids := make([]string, 0, len(skills))
	for id := range skills {
		ids = append(ids, id)
	}
	sort.Strings(ids)

	var sb strings.Builder
	for _, id := range ids {
		relPath := skills[id]
		if strings.TrimSpace(relPath) == "" {
			continue
		}

		path := ResolvePath(relPath)
		if !fileExists(path) {
			continue
		}

		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		// Inline the content but preserve line breaks as literal "\n" markers.
		content := strings.ReplaceAll(string(data), "\r\n", "\\n")
		content = strings.ReplaceAll(content, "\n", "\\n")

		sb.WriteString(fmt.Sprintf(`<skill id="%s">%s</skill>`, id, content))
	}

	if sb.Len() == 0 {
		return ""
	}
	return fmt.Sprintf("<skills>\n    %s\n</skills>", sb.String())
}
