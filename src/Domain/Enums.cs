namespace Neuroset.Domain;

public enum MessageRole { System, User, Assistant, Tool }
public enum MessageStatus { Pending, Streaming, Completed, Failed, Cancelled }
public enum MemoryScope { ShortTerm, LongTerm }
public enum ToolRisk { Safe, NetworkRead, SensitiveRead, Write, Destructive, CodeExecution }
public enum PermissionDecision { Deny, AllowOnce, AllowForChat, AlwaysAllow }
public enum PermissionPolicy { Ask, Allow, Deny }
public enum ScheduleKind { Once, Daily, Weekly, Interval, Cron }
public enum AgentEventKind
{
    TextDelta,
    ToolCallStarted,
    ToolCallArguments,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    Progress,
    Usage,
    Error,
    Completed,
    Recovery
}

public enum AnimationLevel { Full, Reduced, Off }
public enum UiDensity { Compact, Comfortable, Spacious }
public enum ConversationMode { Chat, Coding, Roleplay }
public enum RoleplayPromptKind { Character, Scene, Player }
public enum ReasoningPreference { Auto, Low, High, Max }
public enum ReasoningEffort { Low, Medium, High, XHigh, Max }
public enum SystemPromptPreset { None, Research, Direct, G0dm0d3 }
public enum ParseltongueIntensity { Light, Medium, Heavy }
public enum ParseltongueTechnique { Leetspeak, Unicode, Zwj, MixedCase, Phonetic, Random }
public enum AutoTuneStrategy { Precise, Balanced, Creative, Chaotic, Adaptive }
public enum SamplingContext { General, Factual, Analytical, Coding, Creative, Conversational, Chaotic }
