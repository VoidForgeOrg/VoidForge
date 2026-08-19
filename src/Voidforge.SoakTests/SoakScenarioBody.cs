using Alba;

namespace Voidforge.SoakTests;

// The driver body of a scenario: registers whatever players it needs and drives them (the scenario
// owns player count and scripting). The reusable orchestration around it — snapshot loop, idle-to-window,
// scheduler drain — lives in SoakDriver, so a body only has to describe the story, not the plumbing.
public delegate Task SoakScenarioBody(IAlbaHost host, SoakRecorder recorder, Deadline deadline);
