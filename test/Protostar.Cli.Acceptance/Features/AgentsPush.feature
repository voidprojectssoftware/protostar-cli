Feature: Backing up custom agents to the registry
  As a developer working across machines
  I want protostar agents push to back up my custom agents
  So that an agent I wrote on one machine is not lost with it

  Scenario: agents --help lists the push subcommand
    When I run protostar with "agents --help"
    Then the exit code is 0
    And the output contains "push"

  Scenario: An agent file without a name is refused without asking the registry
    Given a fake claude-code harness
    And an agent file "nameless.md" with no name in the harness
    When I run protostar with "agents push --harness-home {harnessHome} --personal-only --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "nameless.md has no 'name' in"

  Scenario: Pushing to a registry that does not support agents says so instead of failing on a 404
    Given a fake claude-code harness
    And an agent named "reviewer" in the harness
    And I am signed in to a registry whose capabilities are "skills,projects"
    When I run protostar with "agents push --harness-home {harnessHome} --personal-only --registry {registry}"
    Then the exit code is 1
    And the output contains "This registry is too old for agent backups"
    And the registry was asked only for its capabilities

  Scenario: Pushing with no agents on the machine does nothing
    Given a fake claude-code harness
    When I run protostar with "agents push --harness-home {harnessHome} --personal-only --registry https://unused.invalid"
    Then the exit code is 0
    And the output contains "No agents found"
