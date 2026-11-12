Feature: Backing up skills to the registry
  As a developer working across machines
  I want protostar skills push to back up my local skills
  So that every version of a skill I write is kept in my registry

  Scenario: skills --help lists the push subcommand
    When I run protostar with "skills --help"
    Then the exit code is 0
    And the output contains "push"

  Scenario: skills still lists skills by default
    Given a fake claude-code harness
    And a global skill named "alpha" in the harness
    When I run protostar with "skills --harness claude-code --harness-home {harnessHome} --global-only"
    Then the exit code is 0
    And the output contains "alpha"

  Scenario: Pushing without a session asks the user to log in
    Given a fake claude-code harness
    And a global skill named "alpha" in the harness
    When I run protostar with "skills push --harness claude-code --harness-home {harnessHome} --global-only --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "Not logged in"
    And the output contains "auth login"

  Scenario: Pushing a skill that is not on this machine names it
    Given a fake claude-code harness
    And a global skill named "alpha" in the harness
    When I run protostar with "skills push ghost --harness claude-code --harness-home {harnessHome} --global-only --registry https://unused.invalid"
    Then the exit code is 1
    And the output contains "ghost"

  Scenario: Pushing with no skills on the machine does nothing
    Given a fake claude-code harness
    When I run protostar with "skills push --harness claude-code --harness-home {harnessHome} --global-only --registry https://unused.invalid"
    Then the exit code is 0
    And the output contains "No skills found"
