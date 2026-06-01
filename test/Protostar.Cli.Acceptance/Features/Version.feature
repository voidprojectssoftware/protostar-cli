Feature: Reporting the version
  As a user who installed protostar
  I want protostar --version to report a version
  So that I can confirm which build I am running

  Scenario: --version prints a semantic version
    When I run protostar with "--version"
    Then the exit code is 0
    And the output matches "\d+\.\d+\.\d+"
