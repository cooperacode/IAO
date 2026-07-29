package harnessengine

import (
	"strings"
	"testing"
)

func feature(id int, title string, priority int, passes bool) Feature {
	return Feature{Id: id, Title: title, Priority: priority, Passes: passes, DependsOn: []int{}, References: []string{}}
}

func featureDep(id int, title string, priority int, passes bool, deps []int) Feature {
	f := feature(id, title, priority, passes)
	f.DependsOn = deps
	return f
}

func TestFeatures_WriteAndLoad_RoundTrip(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{feature(1, "A", 2, false), feature(2, "B", 1, false)})

	loaded := LoadFeatures()
	if len(loaded) != 2 || loaded[0].Title != "A" {
		t.Fatalf("unexpected loaded features: %+v", loaded)
	}
}

func TestFeatures_ParseRawArray_ForcesPendingAndPreservesFields(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]`)

	if len(features) != 2 {
		t.Fatalf("unexpected count: %d", len(features))
	}
	for _, f := range features {
		if f.Passes {
			t.Fatalf("expected feature to be pending: %+v", f)
		}
	}
	if features[0].Title != "Login" {
		t.Fatalf("unexpected title: %s", features[0].Title)
	}
}

func TestFeatures_ParseWithoutId_Reindexes(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"title":"X","priority":1},{"title":"Y","priority":1}]`)

	if len(features) != 2 || features[0].Id != 1 || features[1].Id != 2 {
		t.Fatalf("unexpected ids: %+v", features)
	}
}

func TestFeatures_ParseInvalidJSON_ReturnsEmptyWithoutPanicking(t *testing.T) {
	isolate(t)

	if len(ParseFeatures("this is not json")) != 0 {
		t.Fatal("expected empty")
	}
	if len(ParseFeatures("[]")) != 0 {
		t.Fatal("expected empty")
	}
}

func TestFeatures_NextPending_PicksHighestPriorityPending(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{
		feature(1, "baixa", 3, false),
		feature(2, "alta", 1, false),
		feature(3, "media", 2, true),
	})

	next := NextPendingFeature()
	if next == nil || next.Id != 2 {
		t.Fatalf("unexpected pick: %+v", next)
	}
}

func TestFeatures_ParseMissingDescriptionAndReferences_NormalizeToEmpty(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"X","priority":1}]`)

	if features[0].Description != "" || len(features[0].References) != 0 {
		t.Fatalf("unexpected feature: %+v", features[0])
	}
}

func TestFeatures_ParsePreservesDescriptionAndReferences(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"X","priority":1,"description":"does Y","references":["RF-003"]}]`)

	if features[0].Description != "does Y" || len(features[0].References) != 1 || features[0].References[0] != "RF-003" {
		t.Fatalf("unexpected feature: %+v", features[0])
	}
}

func TestFeatures_ParseDescriptionAboveCeiling_IsTruncated(t *testing.T) {
	isolate(t)

	longDesc := strings.Repeat("a", DescriptionMaxChars+50)
	features := ParseFeatures(`[{"id":1,"title":"X","priority":1,"description":"` + longDesc + `"}]`)

	if len([]rune(features[0].Description)) != DescriptionMaxChars {
		t.Fatalf("unexpected length: %d", len([]rune(features[0].Description)))
	}
}

func TestFeatures_ParseMissingDependsOn_NormalizesToEmptyArray(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"X","priority":1}]`)

	if len(features[0].DependsOn) != 0 {
		t.Fatalf("unexpected deps: %+v", features[0].DependsOn)
	}
}

func TestFeatures_ParseCyclicDependsOn_ReturnsEmptyWithoutPanicking(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]`)

	if len(features) != 0 {
		t.Fatalf("expected empty, got %+v", features)
	}
}

func TestFeatures_ParseSelfReference_ReturnsEmpty(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"A","priority":1,"dependsOn":[1]}]`)

	if len(features) != 0 {
		t.Fatalf("expected empty, got %+v", features)
	}
}

func TestFeatures_ParseNonExistentDependency_ReturnsEmpty(t *testing.T) {
	isolate(t)

	features := ParseFeatures(`[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]`)

	if len(features) != 0 {
		t.Fatalf("expected empty, got %+v", features)
	}
}

func TestFeatures_LoadLegacyListWithoutDependsOn_DoesNotPanic(t *testing.T) {
	isolate(t)

	if err := ensureDir(".harness"); err != nil {
		t.Fatal(err)
	}
	if err := writeAtomic(featureListFilePath, `{"items":[{"id":1,"title":"A","priority":1,"passes":false}]}`); err != nil {
		t.Fatal(err)
	}

	loaded := LoadFeatures()
	if len(loaded) != 1 || len(loaded[0].DependsOn) != 0 {
		t.Fatalf("unexpected loaded: %+v", loaded)
	}
}

func TestFeatures_NextPending_IgnoresFeatureWithPendingDependency(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{
		feature(1, "foundation", 2, false),
		featureDep(2, "depends on 1", 1, false, []int{1}),
	})

	next := NextPendingFeature()
	if next == nil || next.Id != 1 {
		t.Fatalf("unexpected pick: %+v", next)
	}
}

func TestFeatures_NextPending_ReleasesFeatureAfterDependencyPasses(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{
		feature(1, "foundation", 2, false),
		featureDep(2, "depends on 1", 1, false, []int{1}),
	})
	if NextPendingFeature().Id != 1 {
		t.Fatal("expected feature 1 first")
	}

	MarkFeaturePassed(1)

	if next := NextPendingFeature(); next == nil || next.Id != 2 {
		t.Fatalf("unexpected pick: %+v", next)
	}
}

func TestFeatures_NextPending_AllBlocked_ReturnsNilWithPendingItems(t *testing.T) {
	isolate(t)

	// Cyclic graph written directly via Write (bypassing Parse's validation) — simulates a
	// hand-edited feature_list.json outside the normal flow.
	WriteFeatures([]Feature{
		featureDep(1, "A", 1, false, []int{2}),
		featureDep(2, "B", 2, false, []int{1}),
	})

	if NextPendingFeature() != nil {
		t.Fatal("expected nil pick")
	}
	if PendingFeatureCount() != 2 {
		t.Fatalf("unexpected pending count: %d", PendingFeatureCount())
	}
}

func TestFeatures_MarkPassed_FlipsFeature_AllPassingClosesWhenDone(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{feature(1, "A", 1, false), feature(2, "B", 2, false)})

	MarkFeaturePassed(1)
	if PendingFeatureCount() != 1 || AllFeaturesPassing() {
		t.Fatal("unexpected mid-state")
	}

	MarkFeaturePassed(2)
	if PendingFeatureCount() != 0 || !AllFeaturesPassing() || NextPendingFeature() != nil {
		t.Fatal("expected all passing")
	}
}

func TestFeatures_AllPassing_EmptyList_IsFalse(t *testing.T) {
	isolate(t)

	if AllFeaturesPassing() {
		t.Fatal("expected false for empty list")
	}
}

func TestFeatures_Reset_ClearsList(t *testing.T) {
	isolate(t)

	WriteFeatures([]Feature{feature(1, "A", 1, false)})
	ResetFeatures()

	if len(LoadFeatures()) != 0 {
		t.Fatal("expected empty after reset")
	}
}
