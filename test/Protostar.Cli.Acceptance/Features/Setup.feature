Feature: Setting up a machine in one command
  As a developer on a new machine
  I want protostar setup to sign me in, install hooks, back up and pull my skills, and back up my sessions
  So that the machine is ready to work in minutes

  Scenario: setup --help lists its options
    When I run protostar with "setup --help"
    Then the exit code is 0
    And the output contains "--yes"
    And the output contains "--no-browser"
    And the output contains "--provider"

  Scenario: An unreachable registry fails sign-in but hooks are still installed
    Given a fake claude-code harness
    When I run protostar with "setup --registry https://127.0.0.1:1 --harness-home {harnessHome}"
    Then the exit code is 1
    And the output contains "Sign-in: failed"
    And the output contains "Hooks: done"
    And the output contains "Skills: skipped. Needs sign-in."
    And the output contains "Configuration: skipped"
    And the harness settings contain "capture --hook"

  Scenario: An invalid registry URL stops before any step
    When I run protostar with "setup --registry not-a-url"
    Then the exit code is 1
    And the output contains "Invalid registry URL"
