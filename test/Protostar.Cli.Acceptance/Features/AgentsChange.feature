Feature: Renaming and deleting custom agents in the registry
  As a developer working across machines
  I want protostar agents rename and agents delete
  So that a renamed or deleted agent does not come back from another machine

  Scenario: agents --help lists the rename and delete subcommands
    When I run protostar with "agents --help"
    Then the exit code is 0
    And the output contains "rename"
    And the output contains "delete"

  Scenario: Renaming on a registry without agents says so before looking anything up
    Given I am signed in to a registry whose capabilities are "skills,projects"
    When I run protostar with "agents rename reviewer code-reviewer --personal-only --registry {registry}"
    Then the exit code is 1
    And the output contains "This registry is too old for agent backups"
    And the registry was asked only for its capabilities

  Scenario: Deleting needs a sign-in
    When I run protostar with "agents delete migrator --yes --personal-only --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "Not logged in"
