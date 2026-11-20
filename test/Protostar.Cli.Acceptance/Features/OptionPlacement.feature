Feature: Misplaced and unknown options are refused
  As a user or agent typing a command
  I want an option put before the subcommand, or one the command does not have, to be refused with what works
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

  Scenario Outline: An option before the first command is refused with where it goes
    When I run protostar with "<typed>"
    Then the exit code is not 0
    And the output contains "<option> goes after the subcommand"
    And the output does not contain "Did you mean"
    And the output, with line breaks joined, contains "Run: protostar <corrected>"

    Examples:
      | typed                                                  | option         | corrected                                              |
      | --registry https://localhost:1 skills remote           | --registry     | skills remote --registry https://localhost:1           |
      | --harness-home {harnessHome} sessions status           | --harness-home | sessions status --harness-home                         |
      | --registry https://localhost:1 auth status             | --registry     | auth status --registry https://localhost:1             |
      | --yes setup                                            | --yes          | setup --yes                                            |

  Scenario Outline: Version and help still work at the root
    When I run protostar with "<typed>"
    Then the exit code is 0
    And the output contains "<shown>"

    Examples:
      | typed     | shown   |
      | --version | .       |
      | -v        | .       |
      | --help    | USAGE:  |

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

  Scenario Outline: An option the command does not declare is refused with the closest real one
    Given a fake claude-code harness
    When I run protostar with "<typed>"
    Then the exit code is not 0
    And the output, with line breaks joined, contains "Unknown option <option> for protostar <command>. Did you mean <meant>?"
    And the output, with line breaks joined, contains "protostar <command> --help"
    And the harness has no settings file

    Examples:
      | typed                                                | option     | command       | meant      |
      | skills pull --forse --harness-home {harnessHome}     | --forse    | skills pull   | --force    |
      | sessions list --projct x --harness-home {harnessHome} | --projct   | sessions list | --project  |
      | project link demo --dri x                            | --dri      | project link  | --dir      |
      | auth status --registy x                              | --registy  | auth status   | --registry |
      | setup --yse --harness-home {harnessHome}             | --yse      | setup         | --yes      |
      | skills pull -force --harness-home {harnessHome}      | -force     | skills pull   | --force    |

  Scenario: A version flag after a command points at the root command instead of running it
    Given a global skill named "solo" in the harness
    When I run protostar with "skills --version --harness-home {harnessHome}"
    Then the exit code is not 0
    And the output contains "--version is not an option of protostar skills"
    And the output contains "protostar --version"
    And the output does not contain "solo"

  Scenario Outline: Forms Spectre accepts for a declared option still parse
    Given a global skill named "solo" in the harness
    When I run protostar with "<typed>"
    Then the output does not contain "Unknown option"
    And the output does not contain "is not an option"

    Examples:
      | typed                                                                     |
      | skills -H claude-code --global-only --harness-home {harnessHome}          |
      | skills -H=claude-code --harness-home {harnessHome}                        |
      | sessions list -p=-x -n 3 --harness-home {harnessHome}                     |
      | sessions list --project=-x --harness-home {harnessHome}                   |
      | sessions push --session {harnessHome} --throttle 1 --harness-home {harnessHome} |
      | skills pull --version 3 solo --harness-home {harnessHome}                 |
