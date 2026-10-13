Microsoft's new Decision-1 isn't a chat model. It takes JSON + a question and returns a probability.
I tested it on AIOps data: 94% root-cause accuracy vs 82% for a heuristic, ~240 ms per call.

Available in Microsoft Foundry, it works like this: you send a state plus a typed question, and it returns a number: a probability for yes/no, a probability per option, or a score on a scale. No text, no rationale, about one output token per call. It's meant for the fast decisions around a system: gates, routing, classification.

Root-cause analysis on 90 microservice failures: it picked the right service 94% of the time, vs 82% for a "largest metric deviation" heuristic, at cents for a full run.

Log anomaly detection on HDFS needed more work. Asking "is this abnormal?" over raw event counts wasn't enough. Computing a lifecycle summary, asking "is the lifecycle complete?", and calibrating the threshold on held-out data got it to 86% on 1,200 blocks (one of five variants I tried), ahead of gpt-4.1-nano (79%).

An honest note: a logistic regression trained on 50 labeled blocks hit 95%. With labels, train a classifier. A decision model is for when you don't have them.

What I'd tell someone trying it: calibrate the threshold, compute domain features instead of handing it raw counts, and test it on your own data first.

Full write-up with the signals, a case analysis and results: link in the first comment.

#AIOps #Observability #LLM #MachineLearning

---
First comment (post it right after publishing):

Full write-up with the signals, a case analysis and results: https://dev.to/pedrosakuma/microsoft-decision-1-in-practice-probabilities-for-aiops-decisions-3kj8
