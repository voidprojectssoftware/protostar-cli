Feature: Default command guidance
  As a new user
  I want a bare protostar invocation to explain itself
  So that I know it is working and where to go next

  Scenario: Running with no arguments prints guidance
    When I run protostar with no arguments
    Then the exit code is 0
    And the output contains "Live, continuous refinement of agent skills."
    And the output contains "protostar --help"
