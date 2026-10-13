"""promptevals command line.

    promptevals validate                       check the case set (free)
    promptevals selftest [--judge]             check the harness / graders
    promptevals generate --count happy-path=5 missing-info=3 --notes "..."
    promptevals accept --all | --ids a,b       merge reviewed proposed cases
    promptevals run --variant baseline         sample + grade every case
    promptevals run --variant v1 --note "..."  after editing the system prompt
    promptevals regrade --variant v1           re-grade stored outputs (rubric/checks changed)
    promptevals report                         summary table + runs/<flow>/report.html
    promptevals compare baseline v1            paired per-case comparison
    promptevals human-export --variant v1 --n 20
    promptevals human-import --variant v1 --csv runs/<flow>/v1/human.csv
"""

from __future__ import annotations

import argparse
import asyncio
import re
import sys
from collections import Counter
from pathlib import Path

from .cases import load_cases
from .config import Config, clean_prompt, load_config


def _client():
    from .llm import make_client

    return make_client()


def _split(s: str | None) -> list[str] | None:
    return [x.strip() for x in s.split(",") if x.strip()] if s else None


def _confirm(msg: str, yes: bool) -> None:
    print(msg)
    if yes:
        return
    if not sys.stdin.isatty():
        raise SystemExit("Refusing to make paid API calls without confirmation; re-run with --yes.")
    if input("Proceed? [y/N] ").strip().lower() not in {"y", "yes"}:
        raise SystemExit("Aborted.")


# ---------------------------------------------------------------- commands


def cmd_validate(config: Config, args) -> int:
    cases = load_cases(config.cases_path)
    print(f"{len(cases)} cases in {config.cases_path}")
    tags = Counter((c.tags or ["untagged"])[0] for c in cases)
    print("primary tags: " + ", ".join(f"{t}={n}" for t, n in tags.most_common()))
    norm = Counter(re.sub(r"\W+", " ", c.prompt_text().lower()).strip() for c in cases)
    dups = [k for k, n in norm.items() if n > 1]
    lengths = sorted(len(c.prompt_text().split()) for c in cases)
    print(f"input length (words): min {lengths[0]}, median {lengths[len(lengths) // 2]}, max {lengths[-1]}")
    problems = []
    if dups:
        problems.append(f"{len(dups)} duplicate inputs (after normalising case/punctuation)")
    no_criteria = [c.id for c in cases if not (c.must or c.must_not or c.checks)]
    if no_criteria:
        problems.append(f"no must/must_not/checks (only the rubric can grade these): {no_criteria}")
    placeholders = [c.id for c in cases if re.search(r"\[[^\]]{4,}\]", c.prompt_text())]
    if placeholders:
        problems.append(f"inputs still contain [placeholder] text: {placeholders}")
    multi = sum(1 for c in cases if not isinstance(c.input, str))
    print(f"multi-turn cases: {multi}")
    if len(cases) < 15:
        problems.append(f"only {len(cases)} cases - one flaky case swings the score; aim for 15-100")
    prompt = config.read_system_prompt()
    if re.search(r"\[[^\]]{4,}\]", prompt):
        problems.append("system prompt still contains [placeholder] text")
    n = len(cases) * config.run.reps
    print(f"noise floor on pass rate at {len(cases)} cases x {config.run.reps} reps: ~+/-{100 / max(n, 1) ** 0.5:.0f} pts")
    for p in problems:
        print(f"  ! {p}")
    return 0


def cmd_run(config: Config, args) -> int:
    from .runner import run_variant, select_cases

    prompt = clean_prompt(Path(args.prompt).read_text(encoding="utf-8")) if args.prompt else None
    cases = select_cases(load_cases(config.cases_path), _split(args.ids), _split(args.tags))
    reps = args.reps or config.run.reps
    judged = [g for g in config.grading.graders if g != "code"]
    _confirm(
        f"Run '{args.variant}': {len(cases)} cases x {reps} reps on "
        f"{config.target.provider}/{config.target.model}"
        + (f" (via {config.target.entrypoint})" if config.target.entrypoint else "")
        + f"; graders: {', '.join(config.grading.graders)}"
        + (f" (judge: {config.judge.model})" if judged else "")
        + ". Already-completed (case, rep) pairs are skipped.",
        args.yes,
    )
    summary = asyncio.run(
        run_variant(config, args.variant, _client(), ids=_split(args.ids), tags=_split(args.tags),
                    reps=args.reps, force=args.force, note=args.note, system_prompt=prompt)
    )
    print(f"\n[{summary.variant}] completed {summary.completed}, errors {summary.errors} "
          f"{summary.error_classes or ''}, skipped {summary.skipped}, {summary.wall_s}s")
    from .report import summarize

    print()
    print(summarize(config, [args.variant], show_failures=False))
    return 1 if summary.errors else 0


def cmd_regrade(config: Config, args) -> int:
    from .runner import regrade_variant

    _confirm(f"Re-grade stored outputs of '{args.variant}' with graders: {', '.join(config.grading.graders)}.",
             args.yes)
    failures = asyncio.run(regrade_variant(config, args.variant, _client()))
    print(f"done; {failures} grader errors (those rows keep their previous grades)")
    return 1 if failures else 0


def cmd_report(config: Config, args) -> int:
    from .report import build_html, summarize

    text = summarize(config, _split(args.variants))
    print(text)
    (config.flow_dir / "summary.md").parent.mkdir(parents=True, exist_ok=True)
    (config.flow_dir / "summary.md").write_text(text, encoding="utf-8")
    if not args.no_html:
        html = build_html(config)
        print(f"HTML report: {html}" if html else "HTML report skipped (node not found).")
    return 0


def cmd_compare(config: Config, args) -> int:
    from .report import compare

    print(compare(config, args.a, args.b))
    return 0


def cmd_generate(config: Config, args) -> int:
    from .generate import DEFAULT_CATEGORIES, generate_cases

    categories: dict[str, int] = {}
    for item in args.count or [f"{k}=2" for k in DEFAULT_CATEGORIES]:
        name, _, n = item.partition("=")
        categories[name] = int(n or 1)
    total = sum(categories.values())
    _confirm(f"Generate {total} {args.mode} cases with "
             f"{config.generator.provider}/{config.generator.model}: "
             + ", ".join(f"{k}={v}" for k, v in categories.items()), args.yes)
    path = asyncio.run(generate_cases(config, _client(), categories=categories, notes=args.notes, mode=args.mode))
    print(f"Wrote {path} and {path.with_suffix('.md')}.\n"
          "Review every case (edit or delete in the .json), then: promptevals accept --all")
    return 0


def cmd_accept(config: Config, args) -> int:
    from .generate import accept_cases

    path = Path(args.file) if args.file else config.cases_path.with_name(config.cases_path.stem + ".proposed.json")
    if not path.exists():
        raise SystemExit(f"{path} not found - run `promptevals generate` first")
    if not args.all and not args.ids:
        raise SystemExit("pass --all or --ids a,b")
    added = accept_cases(config, path, None if args.all else _split(args.ids))
    print(f"Added {len(added)} cases to {config.cases_path}: {', '.join(added)}")
    return 0


def cmd_human_export(config: Config, args) -> int:
    from .graders.human import export_sample

    out = Path(args.out) if args.out else config.flow_dir / args.variant / "human.csv"
    n = export_sample(config, args.variant, out, n=args.n)
    print(f"Wrote {n} rows to {out}. Fill in human_pass (1/0) and human_notes, then run human-import.")
    return 0


def cmd_human_import(config: Config, args) -> int:
    from .graders.human import import_labels

    csv_path = Path(args.csv) if args.csv else config.flow_dir / args.variant / "human.csv"
    print(import_labels(config, args.variant, csv_path))
    return 0


def cmd_selftest(config: Config, args) -> int:
    from .selftest import run_judge_check, run_offline

    ok, text = run_offline(config)
    print("Offline harness checks:\n" + text)
    if args.judge:
        _confirm(f"\nJudge check: ~{args.cases * 3 * len(config.grading.graders)} calls to {config.judge.model}.",
                 args.yes)
        jok, jtext = asyncio.run(run_judge_check(config, _client(), args.cases))
        print("\nJudge checks (known-bad responses must fail):\n" + jtext)
        ok &= jok
    print("\nAll checks passed." if ok else "\nSome checks FAILED - see above.")
    return 0 if ok else 1


# ---------------------------------------------------------------- parser


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="promptevals", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", default="evals.toml", help="path to evals.toml (default: ./evals.toml)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("validate", help="check the case set")

    p = sub.add_parser("run", help="sample and grade a prompt variant")
    p.add_argument("--variant", default="baseline", help="'baseline' or v1, v2, ...")
    p.add_argument("--note", help="one-line description of what changed (non-baseline)")
    p.add_argument("--prompt", help="system prompt file to use instead of the configured one")
    p.add_argument("--reps", type=int)
    p.add_argument("--ids", help="comma-separated case ids")
    p.add_argument("--tags", help="comma-separated tags (any match)")
    p.add_argument("--force", action="store_true", help="discard results made with a different prompt")
    p.add_argument("--yes", "-y", action="store_true", help="skip the confirmation prompt")

    p = sub.add_parser("regrade", help="re-run graders on stored outputs")
    p.add_argument("--variant", required=True)
    p.add_argument("--yes", "-y", action="store_true")

    p = sub.add_parser("report", help="summary table and HTML report")
    p.add_argument("--variants", help="comma-separated; default all")
    p.add_argument("--no-html", action="store_true")

    p = sub.add_parser("compare", help="paired comparison of two variants")
    p.add_argument("a")
    p.add_argument("b")

    p = sub.add_parser("generate", help="propose new test cases with Claude")
    p.add_argument("--count", nargs="*", metavar="CATEGORY=N",
                   help="e.g. happy-path=5 adversarial=3 (default: 2 of each built-in category)")
    p.add_argument("--notes", help="what makes a case hard in this domain")
    p.add_argument("--mode", choices=["new", "variations"], default="new")
    p.add_argument("--yes", "-y", action="store_true")

    p = sub.add_parser("accept", help="merge reviewed proposed cases into the case set")
    p.add_argument("--all", action="store_true")
    p.add_argument("--ids")
    p.add_argument("--file", help="proposed file (default: <cases>.proposed.json)")

    p = sub.add_parser("human-export", help="export outputs for human grading")
    p.add_argument("--variant", required=True)
    p.add_argument("--n", type=int, help="stratified sample size (default: all)")
    p.add_argument("--out")

    p = sub.add_parser("human-import", help="import human grades and report judge agreement")
    p.add_argument("--variant", required=True)
    p.add_argument("--csv")

    p = sub.add_parser("selftest", help="verify the harness and graders")
    p.add_argument("--judge", action="store_true", help="also test LLM graders on known-bad responses (paid)")
    p.add_argument("--cases", type=int, default=3)
    p.add_argument("--yes", "-y", action="store_true")

    args = ap.parse_args(argv)
    config = load_config(args.config)
    handler = globals()["cmd_" + args.cmd.replace("-", "_")]
    return handler(config, args)


if __name__ == "__main__":
    sys.exit(main())
