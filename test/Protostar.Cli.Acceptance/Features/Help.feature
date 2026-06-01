Feature: Help lists available commands
  As a user
  I want protostar --help to list the commands
  So that I can discover what protostar can do

  Scenario: --help lists the install and uninstall commands
    When I run protostar with "--help"
    Then the exit code is 0
    And the output contains "install"
    And the output contains "uninstall"
