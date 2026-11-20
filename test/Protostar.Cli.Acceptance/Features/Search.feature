Feature: Search my sessions and memory
  As a developer, or a coding agent working for one
  I want to search everything I have backed up from the CLI
  So that I can find the session where something was decided, or what was learned in another project

  Scenario: Not signed in, search --json gives an error code and nothing on standard output
    When I run protostar with "search parentUuid --json --registry https://not-signed-in.invalid"
    Then the exit code is 1
    And standard output is empty
    And standard error is a JSON error with code "not_signed_in"

  Scenario: An unknown kind is refused before anything runs
    When I run protostar with "search parentUuid --kind skills"
    Then the exit code is 255
    And the output contains "--kind must be session or memory."
