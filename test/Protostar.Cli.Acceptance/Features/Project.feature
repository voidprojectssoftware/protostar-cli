Feature: Project shows the folder it is asked about
  As a user with several checkouts
  I want protostar project to look at the folder I name
  So that I check, link or unlink the folder I meant and not the one I am standing in

  Scenario: --dir after the subcommand names the folder
    When I run protostar with "project show --dir {harnessHome} --json"
    Then the output matches 'workingDirectory":"[^"]*\.claude"'

  Scenario: --dir with no subcommand names the folder
    When I run protostar with "project --dir {harnessHome} --json"
    Then the output matches 'workingDirectory":"[^"]*\.claude"'
