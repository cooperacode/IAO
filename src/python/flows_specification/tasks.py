from __future__ import annotations
from harness_engine import docs_reader, harness_config, state_store
from . import evaluator, prompts, store

STEP_BUDGET = 18
SOURCES_FOLDER = prompts.SOURCES_FOLDER

# Phase -> prompt function, for resuming/advancing to a phase's own (non-retry) prompt.
_PHASE_PROMPTS = {
    "discover": prompts.discover_prompt,
    "product": prompts.product_prompt,
    "analysis": prompts.analysis_prompt,
    "design": prompts.design_prompt,
    "review": prompts.review_prompt,
    "approve": prompts.approve_prompt,
}


def start():
    r = store.load_run()
    resumable = (
        r.get("status") == "in_progress"
        and r.get("phase") in ("discover", "product", "analysis", "design", "review")
    ) or (r.get("status") == "awaiting_approval" and r.get("phase") == "approve")
    if resumable:
        return _PHASE_PROMPTS.get(r.get("phase"), prompts.discover_prompt)()

    store.reset()
    store.save_run(
        {
            "step": 0,
            "status": "in_progress",
            "phase": "discover",
            "counters": {},
            "traceLabel": None,
            "terminalReason": None,
        }
    )
    if docs_reader.has_docs(SOURCES_FOLDER):
        content, files = docs_reader.read(SOURCES_FOLDER)
        store.write_accepted("sources", {"files": files, "content": content})
    return prompts.discover_prompt()


def _advance(phase, prompt_fn):
    r = store.load_run()
    r.update({"step": state_store.load().step, "status": "in_progress", "phase": phase})
    store.save_run(r)
    return prompt_fn()


def discover(_=None):
    x = store.read_proposal("idea")
    if x is None:
        return prompts.discover_retry_prompt(
            [
                f"no readable idea proposal was found at '{prompts.IDEA_PROPOSAL_PATH}' "
                "(missing or not valid JSON)."
            ]
        )
    # Real provenance when `start` ingested a sources bundle; falls back to a fixed tag plus
    # a digest of the idea's own content when the run had no sources folder to draw from.
    sources, sources_digest = store.read_accepted("sources")
    files = (sources or {}).get("files") or []
    source = (
        f"{SOURCES_FOLDER} ({len(files)} file(s)): {', '.join(files)}"
        if files
        else "driver"
    )
    source_digest = sources_digest if sources is not None else store.digest(x)
    e = evaluator.idea(x, source, source_digest)
    if not e["passed"]:
        return prompts.discover_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    store.write_accepted("idea", x)
    return _advance("product", prompts.product_prompt)


def product(_=None):
    x = store.read_proposal("prd")
    if x is None:
        return prompts.product_retry_prompt(
            [
                f"no readable PRD proposal was found at '{prompts.PRD_PROPOSAL_PATH}' "
                "(missing or not valid JSON)."
            ]
        )
    _, parent = store.read_accepted("idea")
    e = evaluator.prd(x, parent or "")
    if not e["passed"]:
        return prompts.product_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    store.write_accepted("prd", x)
    return _advance("analysis", prompts.analysis_prompt)


def analysis(_=None):
    x = store.read_proposal("srs")
    if x is None:
        return prompts.analysis_retry_prompt(
            [
                f"no readable SRS proposal was found at '{prompts.SRS_PROPOSAL_PATH}' "
                "(missing or not valid JSON)."
            ]
        )
    p, pd = store.read_accepted("prd")
    e = evaluator.srs(x, pd or "", [g.get("id") for g in (p or {}).get("goals", [])])
    if not e["passed"]:
        return prompts.analysis_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    store.write_accepted("srs", x)
    return _advance("design", prompts.design_prompt)


def design(_=None):
    x = store.read_proposal("sdd")
    if x is None:
        return prompts.design_retry_prompt(
            [
                f"no readable SDD proposal was found at '{prompts.SDD_PROPOSAL_PATH}' "
                "(missing or not valid JSON)."
            ]
        )
    s, sd = store.read_accepted("srs")
    req = [
        r.get("id")
        for r in (s or {}).get("functionalRequirements", [])
        + (s or {}).get("qualityRequirements", [])
    ]
    e = evaluator.sdd(x, sd or "", req)
    if not e["passed"]:
        return prompts.design_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    store.write_accepted("sdd", x)
    return _advance("review", prompts.review_prompt)


def review(_=None):
    x = store.read_proposal("review")
    if x is None:
        return prompts.review_retry_prompt(
            [
                "no readable readiness verdict proposal was found at "
                f"'{prompts.REVIEW_PROPOSAL_PATH}' (missing or not valid JSON)."
            ]
        )
    s, _ = store.read_accepted("srs")
    d, _ = store.read_accepted("sdd")
    req = [
        r.get("id")
        for r in (s or {}).get("functionalRequirements", [])
        + (s or {}).get("qualityRequirements", [])
    ]
    adrs = [a.get("id") for a in (d or {}).get("adrs", [])]
    e = evaluator.readiness(x, req, adrs)
    if not e["passed"]:
        return prompts.review_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    r = store.load_run()
    if x.get("verdict") == "READY":
        store.write_accepted("readiness", x)
        r.update({"status": "awaiting_approval", "phase": "approve"})
        store.save_run(r)
        return "stop"
    count = r.setdefault("counters", {}).get("recascades", 0)
    if count >= 2:
        r.update(
            {
                "status": "needs_human_decision",
                "phase": "stop",
                "terminalReason": "recascade budget exhausted",
            }
        )
        store.save_run(r)
        return "stop"
    r["counters"]["recascades"] = count + 1
    target = x["verdict"].split(":", 1)[1]
    r.update({"status": "in_progress", "phase": target})
    store.save_run(r)
    prompt_fn = _PHASE_PROMPTS.get(target)
    if prompt_fn is None:
        return prompts.review_retry_prompt(
            [f"READINESS_VERDICT_INVALID: unroutable verdict '{x['verdict']}'"]
        )
    return prompt_fn()


def approve(_=None):
    x = store.read_proposal("approval")
    if x is None:
        return prompts.approve_retry_prompt(
            [
                "no readable approval proposal was found at "
                f"'{prompts.APPROVAL_PROPOSAL_PATH}' (missing or not valid JSON)."
            ]
        )
    current = store.bundle_digest()
    e = evaluator.approval(x, current)
    if not e["passed"]:
        return prompts.approve_retry_prompt(
            [f"{v['code']}: {v['message']}" for v in e["violations"]]
        )
    r = store.load_run()
    if x.get("decision") == "revise":
        r.update({"status": "in_progress", "phase": "review"})
        store.save_run(r)
        return prompts.review_prompt()
    prd, _ = store.read_accepted("prd")
    srs, _ = store.read_accepted("srs")
    sdd, _ = store.read_accepted("sdd")
    readiness, _ = store.read_accepted("readiness")
    if not all((prd, srs, sdd, readiness)):
        r.update(
            {
                "status": "publish_blocked",
                "phase": "stop",
                "terminalReason": "one or more accepted documents are missing",
            }
        )
        store.save_run(r)
        return "stop"

    from . import publisher

    rendered = publisher.render_all(prd, srs, sdd, readiness)
    gate = evaluator.development_readiness(
        prd,
        srs,
        sdd,
        readiness,
        x.get("bundleDigest", ""),
        current,
        rendered,
        harness_config.current().docs_max_chars,
    )
    if not gate["passed"]:
        reason = "; ".join(f"{v['code']}: {v['message']}" for v in gate["violations"])
        r.update(
            {"status": "publish_blocked", "phase": "stop", "terminalReason": reason}
        )
        store.save_run(r)
        return "stop"

    ok, error = publisher.publish(prd, srs, sdd, readiness)
    if not ok:
        r.update(
            {
                "status": "publish_blocked",
                "phase": "stop",
                "terminalReason": error or "publish postcondition failed",
            }
        )
        store.save_run(r)
        return "stop"
    r.update({"status": "completed", "phase": "stop"})
    store.save_run(r)
    return "stop"
