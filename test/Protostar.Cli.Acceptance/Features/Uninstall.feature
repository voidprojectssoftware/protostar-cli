Feature: Uninstalling protostar
  As a user
  I want protostar uninstall to remove an installed binary
  So that I can cleanly remove the tool

  Scenario: Uninstalling removes the installed binary
    Given a clean install sandbox
    When I run protostar with "install --dir {installDir} --no-modify-path --no-hooks"
    And I run protostar with "uninstall --dir {installDir} --no-modify-path --no-hooks"
    Then the exit code is 0
    And the output contains "Removed"
    And no protostar binary exists in the install dir

  Scenario: Uninstalling when nothing is installed is a no-op
    Given a clean install sandbox
    When I run protostar with "uninstall --dir {installDir} --no-modify-path --no-hooks"
    Then the exit code is 0
    And the output contains "Nothing to remove"

  Scenario: Uninstalling also removes capture hooks from the harness
    Given a clean install sandbox
    And a fake claude-code harness
    When I run protostar with "install --dir {installDir} --no-modify-path --harness-home {harnessHome}"
    And I run protostar with "uninstall --dir {installDir} --no-modify-path --harness-home {harnessHome}"
    Then the exit code is 0
    And the harness has 0 protostar PostToolUse hooks
