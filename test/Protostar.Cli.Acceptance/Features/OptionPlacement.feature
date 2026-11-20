Feature: Options typed before a subcommand are refused
  As a user or agent typing a command
  I want an option put before the subcommand to be refused with the line that works
  So that I never get a different action from the one I typed

  Scenario Outline: An option before a subcommand is refused with where it goes
    When I run protostar with "<typed>"
    Then the exit code is not 0
    And the output contains "<option> goes after the subcommand"
    And the output, with line breaks joined, contains "Run: protostar <corrected>"

    Examples:
      | typed                                                  | option         | corrected                                              |
      | skills --global-only push --harness-home {harnessHome} | --global-only  | skills push --global-only --harness-home               |
      | sessions --harness-home {harnessHome} status           | --harness-home | sessions status --harness-home                         |
      | project --dir {harnessHome} show                       | --dir          | project show --dir                                     |
      | auth --registry https://localhost:1 status             | --registry     | auth status --registry https://localhost:1             |

  Scenario: Several misplaced options are all named
    When I run protostar with "skills --project x --global-only push"
    Then the exit code is not 0
    And the output contains "--project, --global-only go after the subcommand"

  Scenario: A branch's default command keeps its own options
    Given a global skill named "solo" in the harness
    When I run protostar with "skills --global-only --harness-home {harnessHome}"
    Then the exit code is 0
    And the output contains "solo"

  Scenario: Help before a subcommand still shows that subcommand's help
    When I run protostar with "skills --global-only push --help"
    Then the exit code is 0
    And the output contains "protostar skills push"
