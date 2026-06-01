Feature: Uninstalling protostar
  As a user
  I want protostar uninstall to remove an installed binary
  So that I can cleanly remove the tool

  Scenario: Uninstalling removes the installed binary
    Given a clean install sandbox
    When I run protostar with "install --dir {installDir} --no-modify-path"
    And I run protostar with "uninstall --dir {installDir} --no-modify-path"
    Then the exit code is 0
    And the output contains "Removed"
    And no protostar binary exists in the install dir

  Scenario: Uninstalling when nothing is installed is a no-op
    Given a clean install sandbox
    When I run protostar with "uninstall --dir {installDir} --no-modify-path"
    Then the exit code is 0
    And the output contains "Nothing to remove"
