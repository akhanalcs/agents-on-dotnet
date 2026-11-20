# Working agreements for this repo

This is my hands-on learning repo for AI agents in modern .NET (Microsoft Agent Framework, Microsoft.Extensions.AI, Aspire, Azure OpenAI, Azure Service Bus, Blob Storage). It's the twin of the agent bullets on my resume.

- **I run the commands.** Tell me what to install or run and why. Don't install tools, scaffold projects, add packages, or run `dotnet new` yourself. Reading files, building to check something, and inspecting running resources are fine.
- **Keep code simple and elegant.** Prefer the smallest design that shows best practice; easy to read, reason about and explain. No clever abstractions or reflection when a plain lambda/loop does it.
  - Comment like `~/RiderProjects/rag-on-dotnet/src/RagChat/RagChat.Evaluation/RagFixture.cs`: a 1–2 line purpose comment per class/method, and short "what/why" comments on non-obvious steps.
  - Prefer Microsoft's packages and established conventions over hand-rolled ones. Verify APIs against the installed package version (the Agent Framework moves fast) instead of guessing.
  - When I've added comments to a file, keep them when you edit it.
- **You write the docs.** Keep `README.md` up to date as we go.
  - Link official docs instead of copying them; only write down what's specific to my setup or what we figured out.
  - Use Mermaid diagrams where a picture builds the mental model, plus a "read the code in this order" list.
  - Keep my voice: step-by-step commands with their output, and short "why" notes. Concise but very clear.
- **Explain for interviews.** Call out design decisions and trade-offs (workflow vs. single agent, fan-out/fan-in, structured output, tools, human-in-the-loop, checkpointing, idempotency, evaluation, security, cost) so I can explain them in an interview.
- **Give me small steps.** Don't dump too much at once.
- One-time context: `notes/handoff.md`.
