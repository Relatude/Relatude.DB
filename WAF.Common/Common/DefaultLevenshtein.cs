namespace WAF.Common;
public class DefaultLevenshtein {
    public static void GetDefaultSearchDistance(int wordLength, out int distance1, out int distance2) {
        if (wordLength < 2) {
            distance1 = 0;
            distance2 = 0;
        } else if (wordLength < 3) {
            distance1 = 0;
            distance2 = 1;
        } else if (wordLength < 5) { 
            distance1 = 0;
            distance2 = 1;
        } else if (wordLength < 10) {
            distance1 = 1;
            distance2 = 2;
        } else if (wordLength < 15) {
            distance1 = 2;
            distance2 = 3;
        } else {
            distance1 = 3;
            distance2 = 4;
        }
    }
}
