namespace OpenClawTray.Pages;

public sealed record ProviderDefinition(
    string Id, string Name, string Icon, string Category,
    string Api, string? DefaultBaseUrl, string DefaultModelId,
    bool ShowBaseUrl, bool ShowModelId);

/// <summary>内置 Provider 目录。涵盖 openclaw 安装向导里的主流 Provider。</summary>
public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderDefinition> BuiltIn = new[]
    {
        // ── 官方 ──
        new ProviderDefinition("anthropic", "Anthropic", "🅰", "official", "anthropic-messages", null, "claude-sonnet-4-6", false, false),
        new ProviderDefinition("openai", "OpenAI", "🟢", "official", "openai-responses", null, "gpt-4o", false, false),
        new ProviderDefinition("google", "Google Gemini", "🔵", "official", "google-generative-ai", null, "gemini-2.5-pro", false, false),

        // ── 国际兼容 ──
        new ProviderDefinition("openrouter", "OpenRouter", "🔀", "compatible", "openai-completions", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4-6", true, true),
        new ProviderDefinition("together", "Together AI", "🤝", "compatible", "openai-completions", "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", true, true),
        new ProviderDefinition("venice", "Venice AI", "🏛️", "compatible", "openai-completions", "https://api.venice.ai/api/v1", "llama-3.3-70b", true, true),
        new ProviderDefinition("deepseek", "DeepSeek", "🐳", "compatible", "openai-completions", "https://api.deepseek.com/v1", "deepseek-chat", false, false),
        new ProviderDefinition("groq", "Groq", "⚡", "compatible", "openai-completions", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true, true),
        new ProviderDefinition("fireworks", "Fireworks", "🎆", "compatible", "openai-completions", "https://api.fireworks.ai/inference/v1", "accounts/fireworks/models/llama-v3p3-70b-instruct", true, true),
        new ProviderDefinition("cerebras", "Cerebras", "🧠", "compatible", "openai-completions", "https://api.cerebras.ai/v1", "llama-3.3-70b", true, true),
        new ProviderDefinition("mistral", "Mistral", "🌬️", "compatible", "openai-completions", "https://api.mistral.ai/v1", "mistral-large-latest", false, false),
        new ProviderDefinition("xai", "xAI Grok", "❌", "compatible", "openai-completions", "https://api.x.ai/v1", "grok-3", false, false),
        new ProviderDefinition("perplexity", "Perplexity", "🔮", "compatible", "openai-completions", "https://api.perplexity.ai", "sonar-pro", false, false),
        new ProviderDefinition("nvidia", "NVIDIA NIM", "💎", "compatible", "openai-completions", "https://integrate.api.nvidia.com/v1", "nvidia/llama-3.1-nemotron-70b-instruct", true, true),
        new ProviderDefinition("deepinfra", "DeepInfra", "🏗️", "compatible", "openai-completions", "https://api.deepinfra.com/v1/openai", "meta-llama/Llama-3.3-70B-Instruct", true, true),
        new ProviderDefinition("novita", "Novita AI", "🚀", "compatible", "openai-completions", "https://api.novita.ai/v3/openai", "deepseek/deepseek-r1", true, true),
        new ProviderDefinition("huggingface", "Hugging Face", "🤗", "compatible", "openai-completions", "https://api-inference.huggingface.co/v1", "meta-llama/Llama-3.3-70B-Instruct", true, true),
        new ProviderDefinition("chutes", "Chutes AI", "🎯", "compatible", "openai-completions", "https://api.chutes.ai/v1", "chutes/deepseek-r1", true, true),
        new ProviderDefinition("arcee", "Arcee AI", "🔥", "compatible", "openai-completions", "https://api.arcee.ai/v1", "arcee-ai/arcee-blitz", true, true),
        new ProviderDefinition("vercel-ai-gateway", "Vercel Gateway", "▲", "compatible", "openai-completions", "https://ai-gateway.vercel.sh/v1", "openai/gpt-4o", true, true),

        // ── 国内 ──
        new ProviderDefinition("zai", "Z.AI 智谱", "✨", "cn", "openai-completions", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7", true, false),
        new ProviderDefinition("xiaomi", "小米 mimo", "📱", "cn", "openai-completions", "https://api.xiaomimimo.com/v1", "mimo-v2.5-pro", true, false),
        new ProviderDefinition("moonshot", "Moonshot Kimi", "🌙", "cn", "openai-completions", "https://api.moonshot.cn/v1", "kimi-k2", true, false),
        new ProviderDefinition("qwen", "通义千问", "🌐", "cn", "openai-completions", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-max", true, false),
        new ProviderDefinition("qianfan", "百度千帆", "🔍", "cn", "openai-completions", "https://qianfan.baidubce.com/v2", "ernie-4.0-8k-latest", true, false),
        new ProviderDefinition("tencent", "腾讯混元", "🐧", "cn", "openai-completions", "https://api.hunyuan.cloud.tencent.com/v1", "hunyuan-turbos-latest", true, false),
        new ProviderDefinition("volcengine", "火山方舟", "⛰️", "cn", "openai-completions", "https://ark.cn-beijing.volces.com/api/v3", "doubao-pro-32k", true, false),
        new ProviderDefinition("minimax", "MiniMax", "📏", "cn", "openai-completions", "https://api.minimax.chat/v1", "abab6.5s-chat", true, false),
        new ProviderDefinition("siliconflow", "硅基流动", "🌊", "cn", "openai-completions", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", true, false),
        new ProviderDefinition("stepfun", "阶跃星辰", "👣", "cn", "openai-completions", "https://api.stepfun.com/v1", "step-2-16k", true, false),

        // ── 本地 ──
        new ProviderDefinition("vllm", "vLLM", "🖥️", "local", "openai-completions", "http://localhost:8000/v1", "meta-llama/Llama-3.3-70B-Instruct", true, true),
        new ProviderDefinition("ollama", "Ollama", "🦙", "local", "openai-completions", "http://localhost:11434/v1", "llama3.2", true, true),
        new ProviderDefinition("lmstudio", "LM Studio", "🎵", "local", "openai-completions", "http://localhost:1234/v1", "local-model", true, true),
        new ProviderDefinition("sglang", "SGLang", "⚡", "local", "openai-completions", "http://localhost:30000/v1", "meta-llama/Llama-3.3-70B-Instruct", true, true),
        new ProviderDefinition("litellm", "LiteLLM Proxy", "🛡️", "local", "openai-completions", "http://localhost:4000/v1", "gpt-4o", true, true),

        // ── 其他 ──
        new ProviderDefinition("github-copilot", "GitHub Copilot", "🐙", "other", "openai-responses", null, "gpt-4o", false, true),
        new ProviderDefinition("bedrock", "AWS Bedrock", "☁️", "other", "openai-completions", null, "anthropic.claude-sonnet-4-6", false, true),

        // ── 自定义 ──
        new ProviderDefinition("custom", "自定义 Provider", "⚙️", "custom", "openai-completions", "", "", true, true),
    };

    public static ProviderDefinition? FindById(string id) =>
        BuiltIn.FirstOrDefault(p => p.Id == id);
}
