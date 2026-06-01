Feature: Installing protostar
  As a user
  I want protostar install to place the binary in a directory
  So that I can run it from my PATH

  Scenario: Installing into a clean sandbox directory
    Given a clean install sandbox
    When I run protostar with "install --dir {installDir} --no-modify-path --no-hooks"
    Then the exit code is 0
    And the output contains "Installed"
    And a protostar binary exists in the install dir

  Scenario: Re-installing into the same directory succeeds
    Given a clean install sandbox
    When I run protostar with "install --dir {installDir} --no-modify-path --no-hooks"
    And I run protostar with "install --dir {installDir} --no-modify-path --no-hooks"
    Then the exit code is 0
    And the output contains "Installed"
    And a protostar binary exists in the install dir

  Scenario: Installing also wires capture hooks into a detected harness
    Given a clean install sandbox
    And a fake claude-code harness
    When I run protostar with "install --dir {installDir} --no-modify-path --harness-home {harnessHome}"
    Then the exit code is 0
    And a protostar binary exists in the install dir
    And the harness settings contain "capture --hook"
