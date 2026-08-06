namespace UsbCopyConsole.Receiver;

//ერთი მიღების ცდის (კავშირი + სამუშაო + პაკეტები) შედეგი
public enum EAttemptOutcome
{
    //სამუშაო წარმატებით დასრულდა
    Completed,

    //კავშირის/ტრანსპორტის პრობლემა — ახალი ცდა დაგვჭირდება
    Retry,

    //საბოლოო შეცდომა — ხელახლა ცდას აზრი არ აქვს
    Fatal
}
