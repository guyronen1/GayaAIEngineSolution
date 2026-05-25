namespace MaiaAI.Core.Enums;

public enum FixActionType
{
    Manual,          // No auto-execution — operator must intervene
    ApiCall,         // HTTP call to a URL; supports {failureId} placeholder in payload
    StoredProcedure, // Execute a SQL stored procedure; payload = "SpName" or "ConnectionName|SpName"
    Script,          // Execute a shell/PowerShell script; payload = "executable [args]"
    SqlScript        // Execute a raw SQL statement; payload = SQL text; {failureId} is replaced at runtime
}
