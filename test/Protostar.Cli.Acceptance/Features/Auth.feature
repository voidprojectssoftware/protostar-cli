Feature: Authenticate to the registry
  As an operator
  I want protostar auth commands
  So that I can sign in to the registry as an identified user

  Scenario: auth --help lists the subcommands
    When I run protostar with "auth --help"
    Then the exit code is 0
    And the output contains "login"
    And the output contains "logout"
    And the output contains "status"

  Scenario: status reports not logged in when there is no stored session
    When I run protostar with "auth status --registry https://unused.invalid"
    Then the exit code is 0
    And the output contains "Not logged in"

  Scenario: status names where the registry came from
    When I run protostar with "auth status --registry https://unused.invalid"
    Then the exit code is 0
    And the output contains "Registry from --registry"

  Scenario Outline: status with a stored session while the registry is down says it could not verify (<token> token)
    Given a stored session for "https://127.0.0.1:3" whose access token is <token>
    When I run protostar with "auth status --registry https://127.0.0.1:3"
    Then the exit code is 0
    And the output contains "Logged in to https://127.0.0.1:3 as octocat"
    And the output contains "could not verify with the registry"
    And the output does not contain "discovery document"

    Examples:
      | token   |
      | current |
      | expired |
