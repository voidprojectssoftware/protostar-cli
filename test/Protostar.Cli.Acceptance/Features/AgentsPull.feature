Feature: Installing custom agents from the registry
  As a developer working across machines
  I want protostar agents remote and agents pull to bring my custom agents to this machine
  So that the agents I rely on are there wherever I work

  Scenario: agents --help lists the remote and pull subcommands
    When I run protostar with "agents --help"
    Then the exit code is 0
    And the output contains "remote"
    And the output contains "pull"

  Scenario: Pulling from a registry without agent downloads says so before writing anything
    Given a fake claude-code harness
    And I am signed in to a registry whose capabilities are "skills,projects,agents"
    When I run protostar with "agents pull --harness-home {harnessHome} --personal-only --registry {registry}"
    Then the exit code is 1
    And the output contains "This registry is too old for reading agents"
    And the registry was asked only for its capabilities

  Scenario: Listing the registry's agents needs a sign-in
    Given a fake claude-code harness
    When I run protostar with "agents remote --harness-home {harnessHome} --personal-only --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "Not logged in"

  Scenario: --version needs exactly one agent name
    When I run protostar with "agents pull --version 2 --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "--version picks a version of one agent"
