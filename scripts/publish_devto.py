"""Create (or update) a dev.to article from blog/draft.md. Always a draft unless --publish is given.

  export DEVTO_API_KEY=...                      # never commit it
  python scripts/publish_devto.py               # create a draft
  python scripts/publish_devto.py --update ID   # update an existing article
"""
import argparse, json, os, pathlib, re, sys, time, urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
TAGS = ["ai", "aiops", "dotnet", "azure"]  # dev.to accepts at most 4
DESCRIPTION = "Testing Microsoft-Decision-1 on HDFS log anomalies and microservice root-cause analysis: what worked, what failed, and what it costs."


def unwrap(text):
    """Join hard-wrapped lines: dev.to renders every single newline as a <br>."""
    block = re.compile(r"^(#|\||```|[-*] |\d+\. |>|!\[)")
    out, in_code = [], False
    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            in_code = not in_code
            out.append(line)
            continue
        prev = out[-1] if out else ""
        can_join = (not in_code and line.strip() and prev.strip() and not block.match(line.lstrip())
                    and not prev.lstrip().startswith(("```", "|", "#", "![")))
        if can_join:
            out[-1] = prev.rstrip() + " " + line.strip()
        else:
            out.append(line)
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update", type=int, help="existing article id")
    ap.add_argument("--publish", action="store_true", help="publish instead of saving a draft")
    ap.add_argument("--dry-run", action="store_true", help="print the payload summary and exit")
    args = ap.parse_args()

    lines = (ROOT / "blog" / "draft.md").read_text(encoding="utf-8").splitlines()
    title = lines[0].removeprefix("# ").strip()
    body = unwrap("\n".join(lines[1:]).lstrip("\n"))
    article = {"title": title, "body_markdown": body, "published": args.publish, "tags": TAGS, "description": DESCRIPTION}
    if args.dry_run:
        print(json.dumps({**article, "body_markdown": f"<{len(body)} chars>"}, indent=2))
        return

    key = os.environ.get("DEVTO_API_KEY")
    if not key:
        sys.exit("DEVTO_API_KEY is not set")
    url = "https://dev.to/api/articles" + (f"/{args.update}" if args.update else "")
    req = urllib.request.Request(url, data=json.dumps({"article": article}).encode(), method="PUT" if args.update else "POST",
                                 headers={"api-key": key, "Content-Type": "application/json", "Accept": "application/vnd.forem.api-v1+json",
                                          "User-Agent": "decision-1-aiops"})
    for attempt in range(1, 4):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                out = json.load(r)
            break
        except urllib.error.HTTPError as e:
            print(f"attempt {attempt}: HTTP {e.code}: {e.read().decode(errors='replace')[:500]}", file=sys.stderr)
            if e.code < 500 or attempt == 3:
                sys.exit(1)
            time.sleep(3 * attempt)
    print(out.get("id"), out.get("url"), "published" if out.get("published") else "draft")


if __name__ == "__main__":
    main()
