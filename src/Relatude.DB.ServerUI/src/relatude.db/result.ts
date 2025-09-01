export class ResultSet<T> {
    isAll: boolean = false;
    totalCount: number = 0;
    capped: boolean = false;
    pageIndex: number = 0;
    pageSize: number = 0;
    pageCount: number = 0;
    count: number = 0;
    durationMs: number = 0;
    values: T[] = [];
}
export class ResultSetSearch<T> extends ResultSet<SearchResultHit<T>> { }
export class SearchResultHit<T> {
    node: T;
    sample: TextSample;
    score: number;
}
export class TextSample {
    samples: WordSample[];
    cutAtStart: boolean;
    cutAtEnd: boolean;
}
export const formatSample = (sample:TextSample, startTag: string, endTag: string, startEllipse: string = "...", endEllipse: string = "...") => {
    let result: string[] = [];
    if (sample.cutAtStart) result.push(startEllipse);
    for (const s of sample.samples) {
        //if (result.length > 0) result.push(" ");
        if (s.isMatch) result.push(startTag);
        result.push(s.fragment);
        if (s.isMatch) result.push(endTag);
    }
    if (sample.cutAtEnd) result.push(endEllipse);
    return result.join("");
}
export class WordSample {
    isMatch: boolean;
    fragment: string;
}
