window.onload = connect;
function start() {
    fetch('/start', { method: 'POST' });
}
class Result {
    constructor() { }
    testName = "";
    running = false;
    count = -1;
    durationMs = 0;
}
class Tester {
    constructor() { }
    testerName = "";
    results = [new Result()];
}
class Status {
    constructor() { }
    running = false;
    testers = [new Tester()];
}
function connect() {
    const tableContainer = document.getElementById('tableContainer');
    const startButton = document.getElementById('startButton');
    tableContainer.innerHTML = 'Loading...';
    const eventSource = new EventSource('/status');
    const num = new Intl.NumberFormat();
    eventSource.onmessage = function (event) {
        let status = new Status();
        status = JSON.parse(event.data);
        startButton.disabled = status.running;
        const table = document.createElement('table');
        const header = table.createTHead();
        const headerRow = header.insertRow(0);
        headerRow.insertCell().innerHTML = ""
        status.testers.forEach((tester) => {
            const cell = headerRow.insertCell()
            cell.style.textAlign = 'center';
            cell.style.fontSize = "20px";
            cell.style.width = "250px";
            cell.innerHTML = "<b>" + tester.testerName + "</b>";
        });
        const body = table.createTBody();
        body.insertRow().insertCell().innerHTML = "&nbsp;"
        if (status.testers?.length > 0) {
            const totalScorePerTester = new Array(status.testers.length);
            const averageScorePerTester = new Array(status.testers.length);
            const testNames = status.testers[0].results.map(t => t.testName); // Assuming all testers have the same test names
            testNames.forEach((testName) => {
                const row = body.insertRow();
                const cell1 = row.insertCell();
                cell1.textContent = decamel(testName);
                status.testers.forEach((tester, testerIndex) => {
                    const cell = row.insertCell();
                    const result = tester.results.filter(r => r.testName == testName)[0];
                    if (result.running) {
                        cell.textContent = '';
                        const score = Math.random() * 100;
                        cell.style.backgroundColor = `rgba(144, 238, 144, ${score / 100})`;
                    } else if (result.count >= 0) {
                        //cell.textContent = `${result.count} in ${result.durationMs}ms (${(result.count / (result.durationMs / 1000)).toFixed(2)} ops/s)`;
                        cell.style.textAlign = 'right';
                        var score = getScore(testName, tester.testerName, status);
                        const ops = Math.round(result.count / (result.durationMs / 1000));
                        let html = "";
                        if (result.count > 1) {
                            html += "<div style=\"margin:20px; float: left; font-size:20px;\">" + score + "%</div>"
                            html += "<div>" + num.format(result.count) + " count</div>"
                            html += "<div>" + num.format(ops) + " ops/s</div>";
                        }
                        html += "<div>" + num.format(Math.round(result.durationMs)) + " ms</div>";
                        cell.innerHTML = html;
                        cell.style.backgroundColor = `rgba(144, 238, 144, ${score / 100})`;
                    }
                });
            });

            {
                body.insertRow().insertCell().innerHTML = "&nbsp;"
                const row = body.insertRow();
                const cell1 = row.insertCell();
                cell1.textContent = "Average relative score";
                status.testers.forEach((tester, testerIndex) => {

                    var averageScore = 0;
                    var numTests = 0;
                    testNames.forEach((testName) => {
                        const result = tester.results.filter(r => r.testName == testName)[0];
                        if (result.count > 1 && !result.running) {
                            const score = getScore(testName, tester.testerName, status);
                            if (score > 0) {
                                averageScore += score;
                                numTests++;
                            }
                        }
                    });
                    averageScore = averageScore / numTests;


                    const cell = row.insertCell();
                    cell.style.textAlign = 'center';
                    cell.style.fontSize = '35px';
                    cell.style.padding = '13px';
                    const score = Math.round(averageScore);
                    cell.innerHTML = score+"%";
                    cell.style.backgroundColor = `rgba(144, 238, 144, ${score / 100})`;
                });
            }


        }
        tableContainer.innerHTML = '';
        tableContainer.appendChild(table);
    }
}

// returns decamelized string with first letter capitalized
function decamel(camel) {
    const result = camel.replace(/([A-Z])/g, " $1");
    return result.charAt(0).toUpperCase() + result.slice(1);
}
function getScore(testName, testerName, status) {
    let s = new Status();
    s = status;
    let opsForThisTester = 0;
    let maxOps = 0;
    s.testers.forEach((tester) => {
        const result = tester.results.filter(r => r.testName == testName)[0];
        if (result.count >= 0) {
            const ops = result.count / (result.durationMs / 1000);
            if (tester.testerName == testerName) opsForThisTester = ops;
            if (ops > maxOps) maxOps = ops;
        }
    });
    if (maxOps > 0) return Math.round((opsForThisTester / maxOps) * 100);
    return 0;
}
