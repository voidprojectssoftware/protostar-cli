Feature: Agents reach my history through an MCP server
  As a coding agent working for a developer
  I want protostar's history and memory as MCP tools
  So that I can find and read past sessions and memory without parsing CLI output

  `protostar mcp` speaks MCP over stdio, so standard output carries the protocol and nothing else. It serves read-only
  tools only, and a call it cannot answer is a tool error with the --json error codes, never a crash.

  Scenario: An agent lists the read-only tools, and a call while signed out is a tool error
    When an MCP client starts protostar with "mcp --registry https://not-signed-in.invalid"
    Then the server offers exactly the tools "search,list_sessions,get_session,get_memory", each read-only
    When the client calls "search" for "migration"
    Then the call is a tool error with code "not_signed_in"
    When the client calls "delete_session" for "7d3c9a52"
    Then the call is refused as an unknown tool
    And the server is still running, and wrote nothing but JSON-RPC to standard output
