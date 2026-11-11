using System.Text;

namespace Threadbook.Core;

/// <summary>The kind of thing a catalog entry points at.</summary>
public enum CatalogKind { McpServer, Skill, PromptPack, Guide }

/// <summary>What an entry can touch once installed, in plain words. Shown before anything is copied.</summary>
public enum CatalogRisk { Files, Commands, Network, ApiKey }

/// <summary>One catalog entry. The catalog is bundled and read-only; entries are starting points to verify at the source, not downloads.</summary>
public sealed record CatalogEntry(
    string Id, string Name, CatalogKind Kind, string Summary, string SourceName, string SourceUrl,
    string License, string Clients, IReadOnlyList<CatalogRisk> Risks, bool StarterPick,
    string? InstallCommand, string? ConfigSnippet, string VerifiedOn);

/// <summary>The built-in catalog of MCP servers, skills, prompt packs and guides, plus search and the note text used when an entry is saved into a notebook.</summary>
public static class Catalog
{
    public const int MaxQueryLength = 200;

    private static CatalogEntry E(string id, string name, CatalogKind kind, string summary, string source, string url,
        string license, string clients, CatalogRisk[] risks, bool pick, string? install, string? config) =>
        new(id, name, kind, summary, source, url, license, clients, risks, pick, install, config, "2026-11-10");

    private static CatalogEntry Ev(string id, string name, CatalogKind kind, string summary, string source, string url,
        string license, string clients, CatalogRisk[] risks, bool pick, string? install, string? config, string verifiedOn) =>
        new(id, name, kind, summary, source, url, license, clients, risks, pick, install, config, verifiedOn);

    /// <summary>Every bundled entry, starter picks first within each kind.</summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } = Array.AsReadOnly(new[]
    {
        // ---- MCP servers ----
        E("mcp-filesystem", "Filesystem (official reference server)", CatalogKind.McpServer,
            "Lets an AI client read and write files, but only inside the folders you list. The safest first server to try.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Files }, true,
            "npx -y @modelcontextprotocol/server-filesystem C:\\Users\\you\\Documents\\prompts",
            "{\n  \"mcpServers\": {\n    \"filesystem\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"C:\\\\Users\\\\you\\\\Documents\\\\prompts\"]\n    }\n  }\n}"),
        E("mcp-github", "GitHub MCP Server", CatalogKind.McpServer,
            "GitHub's official server: repositories, issues, pull requests and Actions from your AI client, using your own token.",
            "GitHub", "https://github.com/github/github-mcp-server", "MIT",
            "Claude, VS Code, Cursor, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, true,
            null,
            "{\n  \"mcpServers\": {\n    \"github\": {\n      \"url\": \"https://api.githubcopilot.com/mcp/\",\n      \"headers\": { \"Authorization\": \"Bearer YOUR_GITHUB_TOKEN\" }\n    }\n  }\n}"),
        E("mcp-playwright", "Playwright MCP", CatalogKind.McpServer,
            "Microsoft's browser automation server. Drives a real browser through readable page snapshots instead of screenshots.",
            "Microsoft", "https://github.com/microsoft/playwright-mcp", "Apache-2.0",
            "Claude, VS Code, Cursor, most MCP clients", new[] { CatalogRisk.Commands, CatalogRisk.Network }, true,
            "npx -y @playwright/mcp@latest",
            "{\n  \"mcpServers\": {\n    \"playwright\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@playwright/mcp@latest\"]\n    }\n  }\n}"),
        E("mcp-chrome-devtools", "Chrome DevTools MCP", CatalogKind.McpServer,
            "Official DevTools server for debugging pages, performance traces and network inspection with your AI client.",
            "Chrome DevTools team", "https://github.com/ChromeDevTools/chrome-devtools-mcp", "Apache-2.0",
            "Claude, VS Code, Cursor, most MCP clients", new[] { CatalogRisk.Commands, CatalogRisk.Network }, false,
            "npx -y chrome-devtools-mcp@latest",
            "{\n  \"mcpServers\": {\n    \"chrome-devtools\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"chrome-devtools-mcp@latest\"]\n    }\n  }\n}"),
        E("mcp-fetch", "Fetch (official reference server)", CatalogKind.McpServer,
            "Fetches a web page and hands it to the AI as Markdown. Simple, single-purpose, easy to reason about.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network }, true,
            "uvx mcp-server-fetch",
            "{\n  \"mcpServers\": {\n    \"fetch\": {\n      \"command\": \"uvx\",\n      \"args\": [\"mcp-server-fetch\"]\n    }\n  }\n}"),
        E("mcp-git", "Git (official reference server)", CatalogKind.McpServer,
            "Reads and works with a local Git repository: status, diffs, log and commits in the repos you point it at.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            "uvx mcp-server-git --repository C:\\path\\to\\repo",
            "{\n  \"mcpServers\": {\n    \"git\": {\n      \"command\": \"uvx\",\n      \"args\": [\"mcp-server-git\", \"--repository\", \"C:\\\\path\\\\to\\\\repo\"]\n    }\n  }\n}"),
        E("mcp-memory", "Memory (official reference server)", CatalogKind.McpServer,
            "A local knowledge-graph memory so a client can remember facts about you and your projects between chats.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Files }, false,
            "npx -y @modelcontextprotocol/server-memory",
            "{\n  \"mcpServers\": {\n    \"memory\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-memory\"]\n    }\n  }\n}"),
        E("mcp-sequential-thinking", "Sequential Thinking (official reference server)", CatalogKind.McpServer,
            "Gives the AI a scratchpad for breaking a hard problem into steps it can revise as it goes.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", Array.Empty<CatalogRisk>(), false,
            "npx -y @modelcontextprotocol/server-sequential-thinking",
            "{\n  \"mcpServers\": {\n    \"sequential-thinking\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-sequential-thinking\"]\n    }\n  }\n}"),
        E("mcp-time", "Time (official reference server)", CatalogKind.McpServer,
            "Current time and timezone conversions. Tiny, safe, and surprisingly often the thing a model gets wrong without it.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", Array.Empty<CatalogRisk>(), false,
            "uvx mcp-server-time",
            "{\n  \"mcpServers\": {\n    \"time\": {\n      \"command\": \"uvx\",\n      \"args\": [\"mcp-server-time\"]\n    }\n  }\n}"),
        E("mcp-context7", "Context7", CatalogKind.McpServer,
            "Looks up current, version-specific documentation for libraries so generated code uses today's API, not last year's.",
            "Upstash", "https://github.com/upstash/context7", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network }, true,
            "npx -y @upstash/context7-mcp",
            "{\n  \"mcpServers\": {\n    \"context7\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@upstash/context7-mcp\"]\n    }\n  }\n}"),
        E("mcp-brave-search", "Brave Search MCP", CatalogKind.McpServer,
            "Web search through Brave's index, from the company itself. Needs a free Brave Search API key.",
            "Brave", "https://github.com/brave/brave-search-mcp-server", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            "npx -y @brave/brave-search-mcp-server",
            "{\n  \"mcpServers\": {\n    \"brave-search\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@brave/brave-search-mcp-server\"],\n      \"env\": { \"BRAVE_API_KEY\": \"YOUR_KEY\" }\n    }\n  }\n}"),
        E("mcp-stripe", "Stripe Agent Toolkit", CatalogKind.McpServer,
            "Stripe's official tools for agents, including MCP access to payments APIs. Use a restricted key, never your main key.",
            "Stripe", "https://github.com/stripe/ai", "MIT",
            "Claude, Cursor, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            "npx -y @stripe/agent-toolkit --tools=all --stripe-secret-key=YOUR_RESTRICTED_KEY",
            null),
        E("mcp-aws", "AWS MCP Servers (awslabs)", CatalogKind.McpServer,
            "AWS's own suite of MCP servers for AWS services, documentation and architecture guidance. Broad; add only the ones you need.",
            "Amazon Web Services", "https://github.com/awslabs/mcp", "Apache-2.0",
            "Claude, Cursor, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null),
        E("mcp-cloudflare", "Cloudflare MCP Servers", CatalogKind.McpServer,
            "Cloudflare's remote MCP servers for Workers, DNS and observability. Connects with your Cloudflare account sign-in.",
            "Cloudflare", "https://github.com/cloudflare/mcp-server-cloudflare", "Apache-2.0",
            "Claude, Cursor, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null),
        E("mcp-official-collection", "Official MCP Reference Servers", CatalogKind.McpServer,
            "The full reference collection from the MCP project itself, plus the archived list. The first place to check before trusting a third-party server.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/servers", "MIT",
            "Any MCP client", new[] { CatalogRisk.Files, CatalogRisk.Commands, CatalogRisk.Network }, true,
            null, null),
        Ev("mcp-notion", "Notion MCP Server", CatalogKind.McpServer,
            "Notion's official MCP server: search, read and update Notion pages and databases from an AI client. Needs a Notion integration token.",
            "Notion", "https://github.com/makenotion/notion-mcp-server", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            "npx -y @notionhq/notion-mcp-server", null, "2026-11-11"),
        Ev("mcp-redis", "Redis MCP Server", CatalogKind.McpServer,
            "Redis's official MCP server: explore and manage data in your own Redis or Redis Cloud with natural-language commands.",
            "Redis", "https://github.com/redis/mcp-redis", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-mongodb", "MongoDB MCP Server", CatalogKind.McpServer,
            "MongoDB's official MCP server: explore schemas, query collections and manage MongoDB databases and Atlas clusters.",
            "MongoDB", "https://github.com/mongodb-js/mongodb-mcp-server", "Apache-2.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-supabase", "Supabase MCP Server", CatalogKind.McpServer,
            "Supabase's official MCP server: manage a Supabase project — tables, SQL, auth and storage — from an AI client.",
            "Supabase", "https://github.com/supabase/mcp", "Apache-2.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-terraform", "Terraform MCP Server", CatalogKind.McpServer,
            "HashiCorp's official MCP server for Terraform: look up registry modules and providers, and work with Terraform Cloud and Enterprise workspaces.",
            "HashiCorp", "https://github.com/hashicorp/terraform-mcp-server", "MPL-2.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            "docker run -i --rm hashicorp/terraform-mcp-server", null, "2026-11-11"),
        Ev("mcp-grafana", "Grafana MCP Server", CatalogKind.McpServer,
            "Grafana's official MCP server: search dashboards, query datasources and investigate alerts, metrics and logs.",
            "Grafana Labs", "https://github.com/grafana/mcp-grafana", "Apache-2.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            "docker run --rm -i grafana/mcp-grafana", null, "2026-11-11"),
        Ev("mcp-huggingface", "Hugging Face MCP Server", CatalogKind.McpServer,
            "Hugging Face's official MCP server: search models, datasets and Spaces, and use hosted inference with your Hugging Face token.",
            "Hugging Face", "https://github.com/huggingface/hf-mcp-server", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-docker", "Docker MCP Gateway", CatalogKind.McpServer,
            "Docker's MCP Gateway and catalog: run vetted MCP servers in containers through one gateway instead of installing each server directly on your machine.",
            "Docker", "https://github.com/docker/mcp-gateway", "MIT",
            "Docker Desktop users, most MCP clients", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),
        Ev("mcp-kubernetes", "Kubernetes MCP Server", CatalogKind.McpServer,
            "An MCP server for Kubernetes and OpenShift, maintained in the Kubernetes community org: inspect and manage clusters, pods and workloads through your own kubeconfig.",
            "Kubernetes community (containers org)", "https://github.com/containers/kubernetes-mcp-server", "Apache-2.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Commands, CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("mcp-blender", "Blender MCP", CatalogKind.McpServer,
            "A community MCP server that controls Blender from an AI client: modelling, materials and rendering driven by prompts. It executes code inside Blender, so treat connected sessions like running any script.",
            "ahujasid (community)", "https://github.com/ahujasid/mcp-for-blender", "MIT",
            "Claude, Cursor, most MCP clients", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),
        Ev("mcp-obsidian", "Obsidian MCP Server", CatalogKind.McpServer,
            "A community MCP server for Obsidian vaults: read, search and edit notes through the Obsidian Local REST API plugin, which must be installed in the vault first.",
            "MarkusPfundstein (community)", "https://github.com/MarkusPfundstein/mcp-obsidian", "MIT",
            "Claude, Cursor, most MCP clients", new[] { CatalogRisk.Files, CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("mcp-jetbrains", "JetBrains MCP Server", CatalogKind.McpServer,
            "JetBrains' MCP server for its IDEs: an AI client can search code, read files and use IDE features in IntelliJ IDEA, PyCharm and the other JetBrains IDEs.",
            "JetBrains", "https://github.com/JetBrains/mcp-jetbrains", "Apache-2.0",
            "JetBrains IDEs with MCP clients", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),
        Ev("mcp-gitlab", "GitLab MCP Server", CatalogKind.McpServer,
            "A broad community GitLab MCP server: projects, merge requests, issues, pipelines and wiki through your GitLab token. Scope the token narrowly — it can act as you.",
            "zereight (community)", "https://github.com/zereight/gitlab-mcp", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-microsoft", "Microsoft MCP Servers (official catalog)", CatalogKind.McpServer,
            "Microsoft's own catalog of its official MCP servers in one repository — the vendor index to check before trusting a third-party server for a Microsoft service. The standalone Azure MCP repository was archived into this catalog.",
            "Microsoft", "https://github.com/microsoft/mcp", "MIT",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network, CatalogRisk.ApiKey }, false,
            null, null, "2026-11-11"),
        Ev("mcp-mslearn", "Microsoft Learn MCP Server", CatalogKind.McpServer,
            "Microsoft's official Learn MCP server: search and read Microsoft documentation (Azure, .NET, Windows and more) so generated answers cite the real docs.",
            "Microsoft", "https://github.com/MicrosoftDocs/mcp", "CC-BY-4.0",
            "Claude, Cursor, VS Code, most MCP clients", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),

        // ---- Skills ----
        E("skill-anthropic-skills", "Claude Skills (official examples)", CatalogKind.Skill,
            "Anthropic's official skill examples: document handling, design, testing and more, in the standard SKILL.md format other tools copy.",
            "Anthropic", "https://github.com/anthropics/skills", "Apache-2.0 (code) / source-available examples",
            "Claude, Claude Code", new[] { CatalogRisk.Files, CatalogRisk.Commands }, true,
            "/plugin marketplace add anthropics/skills",
            null),
        E("skill-awesome-claude-skills", "Awesome Claude Skills", CatalogKind.Skill,
            "A large community-curated list of Claude skills. A discovery aid: read any skill fully before using it, skills are instructions an AI will follow.",
            "ComposioHQ (community)", "https://github.com/ComposioHQ/awesome-claude-skills", "See repository",
            "Claude, Claude Code", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null),
        E("skill-everything-claude-code", "Everything Claude Code", CatalogKind.Skill,
            "A big field-tested collection of agents, skills, commands and rules for Claude Code, gathered from real daily use.",
            "affaan-m (community)", "https://github.com/affaan-m/everything-claude-code", "MIT",
            "Claude Code", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            "git clone https://github.com/affaan-m/everything-claude-code",
            null),

        Ev("skill-openai-skills", "OpenAI Skills Catalog", CatalogKind.Skill,
            "OpenAI's skills catalog for Codex: installable SKILL.md instruction packs that teach the agent a repeatable workflow, in the same format other agents copy.",
            "OpenAI", "https://github.com/openai/skills", "See repository",
            "Codex, SKILL.md-compatible agents", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),
        Ev("skill-vercel-agent-skills", "Vercel Agent Skills", CatalogKind.Skill,
            "Vercel's official collection of agent skills, including React and web-design guidance, written for SKILL.md-compatible agents.",
            "Vercel", "https://github.com/vercel-labs/agent-skills", "See repository",
            "Claude Code, Codex, Cursor, SKILL.md-compatible agents", new[] { CatalogRisk.Files, CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),
        // ---- Prompt packs ----
        E("pack-awesome-chatgpt-prompts", "Awesome ChatGPT Prompts", CatalogKind.PromptPack,
            "One of the largest prompt-pattern collections: role prompts for writers, developers, marketers and more. Useful as examples and starting points.",
            "f (community)", "https://github.com/f/prompts.chat", "CC0-1.0",
            "Any AI chat", Array.Empty<CatalogRisk>(), true,
            null, null),
        E("pack-mcp-registry", "MCP Registry", CatalogKind.PromptPack,
            "The official community registry for discovering public MCP servers. Not a prompt pack: it is where new servers are published first.",
            "Model Context Protocol", "https://registry.modelcontextprotocol.io/", "See site",
            "Any MCP client", new[] { CatalogRisk.Network }, false,
            null, null),

        Ev("pack-claude-cookbooks", "Claude Cookbooks", CatalogKind.PromptPack,
            "Anthropic's cookbook collection: worked notebooks for prompting patterns, retrieval, tool use and classification — copy the pattern, not just the prompt.",
            "Anthropic", "https://github.com/anthropics/claude-cookbooks", "MIT",
            "Any AI developer", Array.Empty<CatalogRisk>(), false,
            null, null, "2026-11-11"),
        // ---- Guides ----
        E("guide-anthropic-prompting", "Anthropic Prompt Engineering Guide", CatalogKind.Guide,
            "The primary-source guide to prompting Claude: clarity, examples, structure and extended thinking. Read this before folklore.",
            "Anthropic", "https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/overview", "See site",
            "Claude", Array.Empty<CatalogRisk>(), true,
            null, null),
        E("guide-openai-prompting", "OpenAI Prompt Engineering Guide", CatalogKind.Guide,
            "OpenAI's own strategy and tactics for GPT-class models, including guidance for reasoning models.",
            "OpenAI", "https://developers.openai.com/api/docs/guides/prompt-engineering", "See site",
            "ChatGPT, OpenAI API", Array.Empty<CatalogRisk>(), true,
            null, null),
        E("guide-building-agents", "Building Effective Agents", CatalogKind.Guide,
            "Anthropic's patterns for agent systems: workflows vs agents, chaining, routing and orchestrator-workers, from what actually worked.",
            "Anthropic", "https://www.anthropic.com/research/building-effective-agents", "See site",
            "Any agent builder", Array.Empty<CatalogRisk>(), false,
            null, null),
        E("guide-context-engineering", "Effective Context Engineering for AI Agents", CatalogKind.Guide,
            "How to treat the context window as a finite resource: compaction, note-taking and sub-agents, from Anthropic's engineers.",
            "Anthropic", "https://www.anthropic.com/research/effective-context-engineering-for-ai-agents", "See site",
            "Any agent builder", Array.Empty<CatalogRisk>(), false,
            null, null),
        E("guide-dair", "Prompt Engineering Guide (DAIR)", CatalogKind.Guide,
            "The comprehensive community guide: techniques, papers, and RAG and agent prompting, kept current.",
            "DAIR.AI (community)", "https://www.promptingguide.ai/", "MIT",
            "Any AI chat", Array.Empty<CatalogRisk>(), false,
            null, null),
        E("guide-mcp-csharp-sdk", "MCP C# SDK", CatalogKind.Guide,
            "The official .NET SDK for building your own MCP servers and clients, maintained with Microsoft. The starting point for a Windows-side server.",
            "Model Context Protocol / Microsoft", "https://github.com/modelcontextprotocol/csharp-sdk", "MIT",
            ".NET developers", new[] { CatalogRisk.Commands }, false,
            "dotnet add package ModelContextProtocol --prerelease",
            null),
        Ev("guide-mcp-spec-docs", "Model Context Protocol — Spec and Docs", CatalogKind.Guide,
            "The protocol itself: concepts (tools, resources, prompts), transports and security guidance, straight from the source. Read this first before judging any server or SDK.",
            "Model Context Protocol", "https://modelcontextprotocol.io/", "See site",
            "Any MCP client or server builder", Array.Empty<CatalogRisk>(), true,
            null, null, "2026-11-11"),
        Ev("guide-mcp-clients", "MCP Clients List (official)", CatalogKind.Guide,
            "Which clients support which MCP features — tools, resources, prompts, sampling — so you can check before assuming your client supports a server.",
            "Model Context Protocol", "https://modelcontextprotocol.io/clients", "See site",
            "Any MCP user", Array.Empty<CatalogRisk>(), false,
            null, null, "2026-11-11"),
        Ev("guide-mcp-python-sdk", "MCP Python SDK", CatalogKind.Guide,
            "The official Python SDK for building MCP servers and clients, in the FastMCP style. The quickest route to a first working server.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/python-sdk", "MIT",
            "Python developers", new[] { CatalogRisk.Commands }, false,
            "pip install mcp",
            null, "2026-11-11"),
        Ev("guide-mcp-typescript-sdk", "MCP TypeScript SDK", CatalogKind.Guide,
            "The official TypeScript SDK for building MCP servers and clients in Node or the browser ecosystem.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/typescript-sdk", "See repository",
            "TypeScript developers", new[] { CatalogRisk.Commands }, false,
            "npm install @modelcontextprotocol/sdk",
            null, "2026-11-11"),
        Ev("guide-mcp-rust-sdk", "MCP Rust SDK (rmcp)", CatalogKind.Guide,
            "The official Rust SDK (rmcp) for building MCP servers and clients with Rust's safety and small binaries.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/rust-sdk", "See repository",
            "Rust developers", new[] { CatalogRisk.Commands }, false,
            "cargo add rmcp",
            null, "2026-11-11"),
        Ev("guide-mcp-go-sdk", "MCP Go SDK", CatalogKind.Guide,
            "The official Go SDK for building MCP servers and clients, maintained with Google.",
            "Model Context Protocol / Google", "https://github.com/modelcontextprotocol/go-sdk", "See repository",
            "Go developers", new[] { CatalogRisk.Commands }, false,
            "go get github.com/modelcontextprotocol/go-sdk",
            null, "2026-11-11"),
        Ev("guide-mcp-java-sdk", "MCP Java SDK", CatalogKind.Guide,
            "The official Java SDK for building MCP servers and clients, built on Spring AI infrastructure.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/java-sdk", "See repository",
            "Java developers", new[] { CatalogRisk.Commands }, false,
            null, null, "2026-11-11"),

        // ---- Directories (discovery only: verify every listing at its own source) ----
        Ev("dir-awesome-mcp-servers", "Awesome MCP Servers", CatalogKind.Guide,
            "A large community-curated list of MCP servers by category. A discovery aid only — verify each server at its own source before installing anything.",
            "punkpeye (community)", "https://github.com/punkpeye/awesome-mcp-servers", "See repository",
            "Any MCP user", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("dir-mcp-so", "MCP.so", CatalogKind.Guide,
            "A community directory of MCP servers and clients, organized by category. Discovery only — verify each listing before use.",
            "MCP.so", "https://mcp.so/", "See site",
            "Any MCP user", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("dir-glama", "Glama MCP Directory", CatalogKind.Guide,
            "A searchable directory of MCP servers with per-server metadata. Discovery only — verify each listing before use.",
            "Glama", "https://glama.ai/mcp/servers", "See site",
            "Any MCP user", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("dir-smithery", "Smithery", CatalogKind.Guide,
            "A hosted registry for discovering and connecting MCP servers. Discovery only — a hosted connection means the service sits between you and the server, so read its terms before connecting an account.",
            "Smithery", "https://smithery.ai/", "See site",
            "Any MCP user", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("dir-pulsemcp", "PulseMCP", CatalogKind.Guide,
            "A directory of MCP servers plus a weekly newsletter tracking the ecosystem. Discovery only — verify each listing before use.",
            "PulseMCP", "https://www.pulsemcp.com/", "See site",
            "Any MCP user", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("guide-openai-cookbook", "OpenAI Cookbook", CatalogKind.Guide,
            "OpenAI's cookbook: worked examples and guides for prompting, retrieval, fine-tuning and the API, maintained by OpenAI.",
            "OpenAI", "https://github.com/openai/openai-cookbook", "MIT",
            "Any AI developer", new[] { CatalogRisk.Network }, false,
            null, null, "2026-11-11"),
        Ev("guide-mcp-inspector", "MCP Inspector", CatalogKind.Guide,
            "The MCP project's official Inspector: a visual testing and debugging tool for MCP servers — call tools and inspect messages before trusting a client with a server.",
            "Model Context Protocol", "https://github.com/modelcontextprotocol/inspector", "See repository",
            "Developers building or vetting MCP servers", new[] { CatalogRisk.Commands, CatalogRisk.Network }, false,
            "npx @modelcontextprotocol/inspector", null, "2026-11-11"),
    });

    /// <summary>Plain label for a risk, shown in the entry detail.</summary>
    public static string RiskLabel(CatalogRisk risk) => risk switch
    {
        CatalogRisk.Files => "Touches files",
        CatalogRisk.Commands => "Runs commands",
        CatalogRisk.Network => "Uses the network",
        CatalogRisk.ApiKey => "Needs an API key or token",
        _ => risk.ToString(),
    };

    /// <summary>Short kind label for lists.</summary>
    public static string KindLabel(CatalogKind kind) => kind switch
    {
        CatalogKind.McpServer => "MCP server",
        CatalogKind.Skill => "Skill",
        CatalogKind.PromptPack => "Prompt pack",
        CatalogKind.Guide => "Guide",
        _ => kind.ToString(),
    };

    /// <summary>Filters entries by kind and a plain-text query over name, summary and source. Query is capped for safety.</summary>
    public static IReadOnlyList<CatalogEntry> Search(string? query, CatalogKind? kind)
    {
        var text = (query ?? "").Trim();
        if (text.Length > MaxQueryLength) text = text[..MaxQueryLength];
        IEnumerable<CatalogEntry> hits = Entries;
        if (kind is not null) hits = hits.Where(e => e.Kind == kind);
        if (text.Length > 0)
            hits = hits.Where(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(text, StringComparison.OrdinalIgnoreCase)
                || e.SourceName.Contains(text, StringComparison.OrdinalIgnoreCase));
        return Array.AsReadOnly(hits.ToArray());
    }

    /// <summary>Builds the Markdown note saved when an entry is added to a notebook: the facts, the exact install/config text, and room for personal notes.</summary>
    public static string ForkText(CatalogEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {entry.Name}");
        sb.AppendLine();
        sb.AppendLine(entry.Summary);
        sb.AppendLine();
        sb.AppendLine($"- Type: {KindLabel(entry.Kind)}");
        sb.AppendLine($"- Source: {entry.SourceName} — {entry.SourceUrl}");
        sb.AppendLine($"- License: {entry.License}");
        sb.AppendLine($"- Works with: {entry.Clients}");
        sb.AppendLine($"- Can touch: {(entry.Risks.Count == 0 ? "Nothing on your machine" : string.Join(", ", entry.Risks.Select(RiskLabel)))}");
        sb.AppendLine($"- Catalog verified: {entry.VerifiedOn}");
        if (entry.InstallCommand is not null) { sb.AppendLine(); sb.AppendLine("## Install"); sb.AppendLine(); sb.AppendLine("```"); sb.AppendLine(entry.InstallCommand); sb.AppendLine("```"); }
        if (entry.ConfigSnippet is not null) { sb.AppendLine(); sb.AppendLine("## Config"); sb.AppendLine(); sb.AppendLine("```json"); sb.AppendLine(entry.ConfigSnippet); sb.AppendLine("```"); }
        sb.AppendLine();
        sb.AppendLine("## My notes");
        sb.AppendLine();
        sb.AppendLine("Verify the entry at its source before installing. Servers and skills run with real permissions.");
        return sb.ToString();
    }
}
