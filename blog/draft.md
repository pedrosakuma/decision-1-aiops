# Microsoft-Decision-1 in practice: probabilities for AIOps decisions

Microsoft-Decision-1 isn't a chat model. You send a `state` plus typed questions, and it returns numbers: a probability
for yes/no (`noul`), a probability per option (`choice`), or a position on an ordered scale (`score`). No prose, no
rationale, about 1 output token per call. That makes it a natural candidate for the decisions around an operations
stack: "is this block of logs anomalous?", "which service is the root cause?".

We tested it on two public AIOps datasets against `gpt-4.1-nano`, a reasoning model (`gpt-6.1-sol`) and non-LLM
baselines. This post walks through the signals, how we framed what to detect, and the results,
including a first attempt that did not work. The code is on [GitHub](https://github.com/pedrosakuma/decision-1-aiops).

![Decision-1 results on root-cause analysis and HDFS anomaly detection](https://raw.githubusercontent.com/pedrosakuma/decision-1-aiops/main/blog/images/results.png)

## Highlights

- **Root-cause ranking was a strong fit.** Decision-1 picked the root-cause service in 94% of 90 failure cases, beating a
  "largest metric deviation" heuristic (82%, p=0.013), at about a third of the chat baseline's latency.
- **Log anomaly detection needed work.** The first framing scored 49%. With a computed summary, a better question and a
  calibrated threshold it reached 86% and led the other LLMs.
- **A small trained classifier still wins when you have labels:** 95% with 50 labeled blocks.
- **Cheap and fast:** about 240 ms p50, and cents for a full run at the prices we found (unverified).

## Part 1: HDFS log anomaly detection

### The signal: what a block's log looks like

The [Loghub](https://github.com/logpai/loghub) HDFS_v1 dataset (free for research use; [Xu et al., SOSP'09](https://people.eecs.berkeley.edu/~jordan/papers/xu-etal-sosp09.pdf); [Zhu et al., ISSRE'23](https://arxiv.org/abs/2008.06448)) groups the logs of a
Hadoop distributed file system by *block*: every block gets an identifier, and everything the cluster logged about it
forms one session. Parsed into 29 event templates, the sequence for one block tells a story:

1. The NameNode **allocates** the block.
2. Three DataNodes (replication factor 3) each **start receiving** it and, when done, log that they **received** it.
3. Each replica's **PacketResponder terminates**, acknowledging the write.
4. Each DataNode reports the block to the NameNode, which logs the block map **update** (`addStoredBlock`).
5. Later, deletions may appear.

We do not reproduce raw log lines: the dataset is unsanitized and licensed for research. We work with event
*templates* and counts only. The dataset also has a `Type` column derived from the label, so we do not use it (it would
leak the answer). Labels come from the dataset's own rules plus manual review.

Sampling matters: real prevalence of anomalies is about 2.9%. Our test set is balanced (600 normal, 600 anomalous), so
accuracy here is *not* what you would see in production.

### What "anomalous" really means

Looking at 300 blocks from our calibration split (150 normal, 150 anomalous) shows that "contains an error" is a bad
definition:

| Pattern | Normal | Anomalous |
|---|---|---|
| Complete lifecycle (1 allocation, 3 receives, 3 acks), no error message | 112 | 14 |
| Complete receive/ack, but an error message is present | 38 | 45 |
| Truncated: 1 or 2 receives, no acks | 0 | 65 |
| Extra receive attempts (4 or 5) | 0 | 26 |

- 38 of 150 normal blocks contain a "got exception while serving" message: a benign event.
- 14 anomalous blocks look complete and log no error at all. In the ones we inspected, the NameNode block-map update
  appears once instead of three times, meaning only one replica was registered.
- Truncated blocks are short, often 4 events, with a "writeBlock received exception".

So the real signal is the lifecycle: expected steps, repeated three times, and a count of what is missing. A rule like
"any exception event" gets 71% on the test split (it flags the benign exceptions and misses the quiet anomalies).

### A first attempt that failed

Our first prompt was the obvious one. The state was the list of event templates with counts, and the question was
"does this block's activity indicate an anomaly?". Decision-1 scored **49%** (AUC 0.68), worse than the 71% rule.
Looking at the saved outputs:

- Its probability correlated 0.43 with the number of events, but only 0.14 with the label.
- Short blocks (10 events or fewer), all anomalies, got a mean P(anomaly) of 0.45. Blocks of 15 to 20 events, 99%
  normal, got 0.66.
- Error keywords ("exception") pushed the probability up, whether or not they mattered.

The model reacts to lexical cues and volume, and treats *missing* events as healthy. That is a framing problem, not
a reason to discard the model.

### Modeling what we want to detect

We changed two things, and tested them on the calibration split (never the test split):

1. **State:** we added a computed `lifecycle_summary` (expected replicas = 3; allocations; replica writes started and
   completed; terminated acks; replicas registered with the NameNode; missing lifecycle steps; error events). This is
   feature engineering from HDFS domain knowledge, not something the model derives.
2. **Question:** instead of "is it abnormal?", ask the positive, structural one: "is the block's lifecycle complete?"

Here is a real (short) block as sent. The first-attempt request carried only the part up to `event_counts`; the
enriched one adds `lifecycle_summary`. This is a hard one: the lifecycle looks complete, and there is one "Unexpected
error" event, but the dataset labels the block **anomalous**. The same pattern is also common among normal blocks
(see the table above), which is why error keywords alone are a weak signal.

```json
{
  "model": "Decision-1",
  "state": {
    "system": "HDFS (Hadoop distributed file system), log events for a single data block",
    "total_events": 20,
    "event_counts": {
      "BLOCK* NameSystem allocateBlock:": 1,
      "Receiving block src: dest:": 3,
      "Received block of size from": 3,
      "PacketResponder for block terminating": 3,
      "BLOCK* NameSystem addStoredBlock: blockMap updated: is added to size": 3,
      "BLOCK* NameSystem delete: is added to invalidSet of": 3,
      "Deleting block file": 3,
      "Unexpected error trying to delete block BlockInfo not found in volumeMap": 1
    },
    "lifecycle_summary": {
      "expected_replicas": 3,
      "block_allocations": 1,
      "replica_writes_started": 3,
      "replica_writes_completed": 3,
      "pipeline_acks_terminated": 3,
      "replicas_registered_with_namenode": 3,
      "missing_lifecycle_steps": [],
      "exception_or_error_events": 1
    }
  },
  "questions": {
    "q": {
      "type": "noul",
      "instructions": "Did this block complete the normal HDFS write lifecycle on all expected replicas (allocated, written, acknowledged and registered)?"
    }
  }
}
```

The answer is a single probability that the lifecycle is complete; the anomaly score is `1 - P(complete)`. For a
truncated block, `missing_lifecycle_steps` would instead list entries such as "write completed on every replica".

| Variant (200 calibration blocks) | Accuracy @0.5 | AUC |
|---|---|---|
| "abnormal?", raw counts | 49.5% | 0.65 |
| "is the lifecycle complete?", raw counts | 60.5% | 0.82 |
| "abnormal?", counts + lifecycle summary | 73.5% | 0.91 |
| choice normal / anomalous / uncertain + summary | 79.0% | n/a |
| "lifecycle complete?" + summary | 70.5% | 0.97 |

Both the state and the wording matter. The last variant ranks best (AUC 0.97, mean P(complete) 0.96 for normal and 0.48
for anomalous blocks), but 0.5 is the wrong threshold for it, so it needs calibrating.

### Results on 1,200 held-out blocks

We picked the best variants on the calibration split and scored them on the full test split, with the same enriched
state for every LLM and the threshold chosen on the calibration split only:

| Model | "Complete?" @0.5 | "Complete?", calibrated | Choice with abstain | AUC |
|---|---|---|---|---|
| Decision-1 | 69% | **86%** (thr 0.15) | 79% | 0.964 |
| gpt-4.1-nano | 65% | 79% | 66% | 0.839 |
| gpt-6.1-sol (n=100) | 66% | 66% | 79% | 0.790 |

The choice variant never abstained (0 of 1,200). The reasoning model ran on only 100 blocks (CI roughly 70 to 86%),
and its self-reported confidence was too coarse for calibration to help.

Against non-LLM references on the same blocks:

| Reference | Accuracy |
|---|---|
| Rule: any exception event | 70.8% |
| Logistic regression, 50 labeled blocks | 95.1% |
| Logistic regression, 200 labeled blocks | 97.5% |
| Logistic regression, 8,000 labeled blocks | 99.7% |

**Note: the logistic regression is not a like-for-like comparison.** It is a trained classifier: it needs labeled
examples (the rows above are trained on 50, 200 and 8,000 labeled blocks), a training step, and a stable event schema,
and it only sees raw event counts. Decision-1 and the other LLMs ran zero-shot with no training; we used labels only to
pick a threshold (150 + 150 blocks), and they received our computed lifecycle summary on top of the counts.

Decision-1 beat the rule (p<0.0001) but **not** a logistic regression on event counts. If you have labels and a stable
schema, train a classifier. A zero-shot decision model fits when you do not have labels, or when the schema changes
too often to retrain.

## Part 2: root-cause analysis on microservices

### The signal

[RCAEval](https://github.com/phamquiluan/RCAEval) RE2-OB (MIT license) injects faults into the [Online Boutique](https://github.com/GoogleCloudPlatform/microservices-demo) demo (11 microservices on Kubernetes) and records
three kinds of telemetry while it does:

- **Metrics:** CPU, memory, sockets, disk, request latency (p50/p90) and error rate per service.
- **Traces:** how long each service's spans take.
- **Logs:** how many log lines per second each service emits.

Each case injects one fault into one service, at a known moment. There are 5 root-cause services (checkout, currency,
email, product catalog, recommendation) and 6 fault types (CPU, memory, disk, socket exhaustion, added delay, packet
loss), each repeated 3 times: 90 cases.

### What an on-call engineer would see

We do not hand the model raw time series. A script reduces each case's telemetry to a compact JSON state, and that state
is the only thing Decision-1 sees:

- **Window:** the 12 minutes before and the 12 minutes after the injection time. The state says an alert fired at the
  start of the "after" window; in a real incident you would use your alert time, which is less exact than the dataset's.
- **Metrics:** for every service and metric (CPU, memory, sockets, disk, latency p50/p90, errors) we compare the
  after-mean with the before-mean, scaled by how much the metric varied before the fault. Only the 10 largest shifts are
  kept, each with its before, after and % change.
- **Traces:** mean span duration per service before and after, keeping the 6 largest relative changes.
- **Logs:** log lines per second per service before and after, keeping the 6 largest relative changes.

We compute these deltas ourselves because Decision-1 is weak at arithmetic over raw numbers. The metric list is already
sorted by that shift score, so the first entry is exactly the pick of the baseline rule below. What Decision-1 adds is
combining that list with the trace and log evidence.

**A clear case: CPU fault on checkout.** The top change is the checkout service's CPU going from 0.43 to 18.3 (+4,160%),
followed by its memory (+38%) and sockets (+22%); the entries that follow are changes of a few percent. One service
lights up on its own metrics: the signature is easy to read.

**A harder case: added delay on the email service.** The email service's own p90 latency rises +4,500%, but the
service that *calls* it, checkout, also slows down (p90 +106%, p50 +195%), and checkout's span time triples (14 to 43 ms)
while email's own span time barely moves (0.17 to 0.18 ms). Both services appear in the top changes, and a reader can
mistake the caller for the culprit.

**Packet loss is noisy.** Loss on the product catalog service pushes latency up on the services that depend on it
(recommendation p90 +68,900%), and the frontend starts reporting errors, so the loudest symptoms are not on the
service at fault.

### What we ask

Two questions per case, and each is a `choice` question:

1. Which of the 11 services is the root cause?
2. Which of the 6 fault types was injected?

This is the whole request for the first question (lists shortened). The case id, which contains the true service and
fault, is never sent:

```json
{
  "model": "Decision-1",
  "state": {
    "system": "Online Boutique microservices on Kubernetes (11 services). An alert fired at the start of the 'after' window; telemetry is summarized as before vs after.",
    "minutes_before_and_after": [12.0, 12.0],
    "top_metric_changes": [
      {"service": "checkoutservice", "metric": "cpu", "before": 0.429, "after": 18.3, "change_pct": 4160.0}
    ],
    "trace_span_duration_by_service": [
      {"service": "checkoutservice", "mean_span_ms_before": 12.1, "mean_span_ms_after": 340.5, "change_pct": 2714.0}
    ],
    "log_volume_by_service": [
      {"service": "frontend", "log_lines_per_s_before": 9.8, "log_lines_per_s_after": 14.2, "change_pct": 44.9}
    ]
  },
  "questions": {
    "q": {
      "type": "choice",
      "instructions": "Which service is the root cause of the incident?",
      "criteria": {
        "adservice": "The ad service is the origin of the fault",
        "checkoutservice": "The checkout service is the origin of the fault"
      }
    }
  }
}
```

The second call reuses the same state and asks "What kind of fault was injected into the root-cause service?" with
the options `cpu`, `mem`, `disk`, `delay`, `loss` and `socket`. The response is a probability per option, and we take
the highest. The chat baselines receive the same state and options as prompt text.

The non-LLM reference is a "largest shift" rule: pick the service of the single metric that moved most relative to its
pre-fault variability (the first entry in the state). It needs no training, and it is right 82% of the time.

### Results (90 cases)

| Task | Decision-1 | gpt-4.1-nano | gpt-6.1-sol | Reference rule |
|---|---|---|---|---|
| Root-cause service (11 options) | 94% | 91% | 100% | 82% |
| Fault type (6 options) | 51% | 47% | 54% | 40% |
| p50 latency per call | 240 ms | 728 ms | 2,052 ms | n/a |

Decision-1 beat the heuristic on the service (McNemar p=0.013). Differences against the two LLMs are not significant
(vs gpt-4.1-nano p=0.55, vs gpt-6.1-sol p=0.06), and fault-type differences are not significant for anyone. Only
5 of the 11 services ever appear as a true root cause, which makes the task somewhat easier than it looks.

### Where the model helps, and where it does not

**Service (85 of 90 right).** The heuristic and Decision-1 disagree on 17 cases. In 14, Decision-1 is right and the
rule is wrong: the rule had picked a symptom (the frontend in 9 of them, an email or other service in the rest), mostly on
packet-loss and delay faults, where the loudest numbers sit downstream of the fault. In 3, the rule is right and
Decision-1 is wrong: all three are the email-delay case above, where Decision-1 named checkout, the caller. Its other two
errors were a packet-loss case (it chose the frontend) and a product-catalog loss case (it chose recommendation).
Those are the same "symptom versus cause" confusions.

**Fault type (46 of 90 right).** Per-type results show a clear split:

| Injected fault | Correct (of 15) | Most common wrong answer |
|---|---|---|
| CPU | 13 | disk (2) |
| Delay | 13 | memory, disk (1 each) |
| Memory | 9 | CPU (4) |
| Disk | 9 | memory (4) |
| Packet loss | 0 | memory (9), delay (5) |
| Socket | 2 | CPU (8) |

CPU and delay leave a distinctive trace. Packet loss and socket exhaustion do not: the same latency and error
symptoms show up as other faults, and Decision-1 never answered "loss" once and chose "socket" only twice. Nothing in
the state we give it says "packets were dropped", so this is as much a limit of our summary as of the model. The other
two LLMs landed at 47% and 54%, so this is a hard task for all of them.

**Takeaway for practice.** Use it to rank *where* to look, not to name the fault. It narrowed 11 services to the right
one 94% of the time in a quarter of a second; telling you why is a job for a human, or for richer state.

## Cost and latency

Token counts from the main run (1,380 calls for Decision-1 and gpt-4.1-nano, 280 for the reasoning model; calibration
calls excluded), at list rates we have not verified (Decision-1 as reported by third-party aggregators, gpt-6.1-sol
from OpenAI's list; Azure prices may differ, and hidden reasoning tokens may add to the reasoning model's bill):

| Model | Input / output tokens | Approx. cost | Per call |
|---|---|---|---|
| Decision-1 (about $0.042 per 1M input, output free) | 353k / 1.4k | ~$0.015 | ~$0.00001 |
| gpt-4.1-nano (~$0.10 in / $0.40 out per 1M) | 402k / 18k | ~$0.05 to 0.07 | ~$0.00004 |
| gpt-6.1-sol ($2 in / $10 out per 1M) | 192k / 18k | ~$0.56 | ~$0.002 |

At equal volume the reasoning model would cost about two orders of magnitude more than Decision-1, plus 2 s of latency
per call versus 0.24 s. Check Azure Cost Management for the real bill.

## Caveats

- The HDFS lifecycle summary was written by us from domain knowledge (replication factor 3), so it is feature
  engineering. We tried five variants and chose after seeing calibration results, which makes the headline 86%
  somewhat optimistic. The sample is balanced and the reasoning model ran on 100 blocks only.
- Our pattern table comes from a 300-block calibration split; the 14 "quiet" anomalies were inspected by us and are not
  proof of a general rule.
- RCA has 90 cases and only 5 true root-cause services; samples are small and margins wide. The state is pre-ranked by
  the same shift score the baseline rule uses, and uses the dataset's exact injection time.
- One baseline per model family, temperature 0, our prompt, no few-shot examples. The chat models' "probability" is
  self-reported confidence, so it is coarser than Decision-1's.
- We did not verify published scores for these datasets, so we cite none. Prices are unverified.
- Decision-1 returns no explanation, so keep a human in the loop for consequential decisions.

## Takeaways

- Decision-1 works best when the state already contains the facts that matter. Compute lifecycle checks and deltas
  instead of handing it raw counts.
- Ask the structural question ("is the lifecycle complete?"), not the vague one ("is this abnormal?").
- Calibrate the threshold on held-out labeled data; 0.5 was a poor default.
- With labeled data and a stable schema, a small trained classifier beat every LLM on HDFS. Use a decision model where
  you have no labels, and for fast gates like root-cause ranking.
- Test every gate on your own data before you automate it.

## Links

- Code and reproduction steps: [pedrosakuma/decision-1-aiops](https://github.com/pedrosakuma/decision-1-aiops)

- Loghub HDFS_v1: [Loghub on GitHub](https://github.com/logpai/loghub), [HDFS_v1 on Zenodo](https://zenodo.org/records/8196385)
- Xu et al., [Detecting Large-Scale System Problems by Mining Console Logs](https://people.eecs.berkeley.edu/~jordan/papers/xu-etal-sosp09.pdf) (SOSP 2009)
- Zhu et al., [Loghub: A Large Collection of System Log Datasets for AI-driven Log Analytics](https://arxiv.org/abs/2008.06448) (ISSRE 2023)
- RCAEval: [paper](https://huggingface.co/papers/2412.17015) (WWW 2025), [code](https://github.com/phamquiluan/RCAEval), [dataset on Hugging Face](https://huggingface.co/datasets/phamquiluan/RCAEval)
- [Online Boutique demo](https://github.com/GoogleCloudPlatform/microservices-demo)
