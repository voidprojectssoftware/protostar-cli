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
