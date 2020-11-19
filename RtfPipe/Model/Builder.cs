using RtfPipe.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RtfPipe.Model
{
  internal class Builder
  {
    public RtfHtml Build(Document document)
    {
      var body = new List<IToken>();
      var defaultStyles = new List<IToken>
      {
        new FontSize(UnitValue.FromHalfPoint(24))
      };
      var result = new RtfHtml();

      if (document.ColorTable.Any())
        defaultStyles.Add(new ForegroundColor(document.ColorTable.First()));
      else
        defaultStyles.Add(new ForegroundColor(new ColorValue(0, 0, 0)));

      foreach (var token in document.Contents)
      {
        if (token is DefaultFontRef defaultFontRef)
        {
          var defaultFont = document.FontTable.TryGetValue(defaultFontRef.Value, out var font) ? font : document.FontTable.FirstOrDefault().Value;
          if (defaultFont != null)
            defaultStyles.Add(defaultFont);
        }
        else if (token is DefaultTabWidth tabWidth)
        {
          result.DefaultTabWidth = tabWidth.Value;
        }
        else if (token is Group group)
        {
          if (group.Destination?.Type != TokenType.HeaderTag)
          {
            body.Add(token);
          }
        }
        else if (token.Type != TokenType.HeaderTag)
        {
          body.Add(token);
        }
      }

      result.Root = Build(body, document, defaultStyles);
      return result;
    }

    private Element Build(List<IToken> body, Document document, List<IToken> defaultStyles)
    {
      var attachmentIndex = 0;
      var footnotes = new List<IToken>();
      var footnoteIndex = 0;
      var lastParagraphStyle = default(TokenState);
      var lastBreak = default(IToken);

      var root = new Element(ElementType.Document, new Element(ElementType.Section, new Element(ElementType.Paragraph)));
      var paragraph = root.Elements().First().Elements().First();
      root.SetStyles(defaultStyles);
      root.Elements().First().SetStyles(defaultStyles);

      var groups = new Stack<TokenState>();
      groups.Push(new TokenState(body, defaultStyles));
      
      while (groups.Count > 0)
      {
        while (groups.Peek().Tokens.MoveNext())
        {
          var token = groups.Peek().Tokens.Current;
          if (token is Group childGroup)
          {
            var dest = childGroup.Destination;
            var fallbackDest = default(IWord);
            if (childGroup.Contents.Count > 1
              && (childGroup.Contents[0] is TextToken ignoreText && ignoreText.Value == "*"))
              fallbackDest = childGroup.Contents[1] as IWord;

            if (childGroup.Contents.Count > 1
              && childGroup.Contents[0] is IgnoreUnrecognized 
              && (childGroup.Contents[1].GetType().Name == "GenericTag" || childGroup.Contents[1].GetType().Name == "GenericWord"))
            {
              // Ignore groups with the "skip if unrecognized" flag
            }
            else if (fallbackDest?.GetType().Name == "GenericTag" || fallbackDest?.GetType().Name == "GenericWord")
            {
              groups.Push(new TokenState(new[] { childGroup.Contents[0] }, groups.Peek()));
            }
            else if (dest is ListTextFallback)
            {
              paragraph.Type = ElementType.ListItem;
            }
            else if (dest is NumberingTextFallback
              || dest?.Type == TokenType.HeaderTag
              || fallbackDest?.Type == TokenType.HeaderTag
              || dest is NoNestedTables
              || dest is BookmarkEnd)
            {
              // Ignore fallback content
            }
            else if (dest is Header
              || dest is HeaderEven
              || dest is HeaderFirst
              || dest is HeaderOdd
              || dest is Footer
              || dest is FooterFirst
              || dest is FooterOdd)
            {
              // skip for now
            }
            else if (dest is FieldInstructions || fallbackDest is FieldInstructions)
            {
              var instructions = childGroup.Contents
                .OfType<Group>()
                .SelectMany(g => g.Contents.OfType<TextToken>())
                .FirstOrDefault(t => t.Value?.IndexOf("HYPERLINK") >= 0)
                ?.Value?.Trim();
              if (!string.IsNullOrEmpty(instructions))
              {
                var args = instructions.Split(' ');
                if (args[0] == "HYPERLINK")
                  groups.Peek().AddStyle(new HyperlinkToken(args));
              }
            }
            else if (dest is BookmarkStart)
            {
              paragraph.Add(new Anchor(AnchorType.Bookmark, childGroup.Contents.OfType<TextToken>().FirstOrDefault()?.Value));
            }
            else if (dest is PictureTag)
            {
              paragraph.Add(new Picture(childGroup));
            }
            else if (dest is Footnote)
            {
              var footnoteId = "footnote" + footnoteIndex.ToString("D2");

              if (footnotes.Count > 0)
                footnotes.Add(new ParagraphBreak());

              ProcessFootnoteGroups(childGroup.Contents, footnotes, footnoteIndex);
            }
            else if (dest is ParagraphNumbering)
            {
              paragraph.Type = ElementType.ListItem;
              foreach (var child in childGroup.Contents.Where(t => t.Type == TokenType.ParagraphFormat))
                groups.Peek().AddStyle(child);
            }
            else
            {
              groups.Push(new TokenState(childGroup.Contents, groups.Peek()));
            }
          }
          else if (token is TextToken text)
          {
            var newRun = new Run(text.Value, groups.Peek().Styles);
            if (!newRun.Styles.OfType<IsHidden>().Any())
            {
              if (paragraph.Nodes().LastOrDefault() is Run run
                && run.Styles.CollectionEquals(newRun.Styles))
              {
                run.Value += text.Value;
              }
              else
              {
                paragraph.Add(newRun);
              }
            }
          }
          else if (token is FootnoteReference)
          {
            footnoteIndex++;
            var styleList = new StyleList(groups.Peek().Styles);
            styleList.Merge(new HyperlinkToken()
            {
              Url = $"#footnote{footnoteIndex:d2}"
            });
            paragraph.Add(new Run(footnoteIndex.ToString(), styleList));
          }
          else if (token is LineBreak)
          {
            if (paragraph.Nodes().LastOrDefault() is Run run
              && run.Styles.OfType<FontSize>().FirstOrDefault()?.Value
                == groups.Peek().Styles.OfType<FontSize>().FirstOrDefault()?.Value)
            {
              run.Value += "\n";
            }
            else
            {
              paragraph.Add(new Run("\n", groups.Peek().Styles));
            }
          }
          else if (token is Tab)
          {
            if (paragraph.Nodes().LastOrDefault() is Run run
              && run.Styles.OfType<FontSize>().FirstOrDefault()?.Value
                == groups.Peek().Styles.OfType<FontSize>().FirstOrDefault()?.Value)
            {
              run.Value += "\t";
            }
            else
            {
              paragraph.Add(new Run("\t", groups.Peek().Styles));
            }
          }
          else if (token is PageBreak || token is SectionBreak)
          {
            lastParagraphStyle = null;
            paragraph.SetStyles(groups.Peek().NormalizedStyles(document));

            AddFootnotes(root, footnotes, document, defaultStyles);

            paragraph = new Element(ElementType.Paragraph);
            var section = new Element(ElementType.Section, paragraph);
            section.SetStyles(groups.Peek().NormalizedStyles(document)
              .Where(t => t.Type != TokenType.ParagraphFormat && t.Type != TokenType.RowFormat && t.Type != TokenType.CellFormat));
            root.Add(section);
          }
          else if (token is ParagraphBreak)
          {
            lastParagraphStyle = null;
            if (!paragraph.Styles.Any())
              paragraph.SetStyles(groups.Peek().NormalizedStyles(document));
            
            var nextParagraph = new Element(ElementType.Paragraph);
            paragraph.Parent.Add(nextParagraph);
            paragraph = nextParagraph;
          }
          else if (token is CellBreak || token is NestedCellBreak)
          {
            lastParagraphStyle = null;
            paragraph.SetStyles(groups.Peek().NormalizedStyles(document));

            var parent = paragraph.Parent;
            var cellContent = parent.Elements().Reverse()
              .TakeWhile(e => e.Type != ElementType.TableCell 
                && e.Styles.OfType<InTable>().Any()
                && e.TableLevel == paragraph.TableLevel).Reverse()
              .ToArray();

            // Eliminate empty paragraphs
            if (cellContent.Length > 1
              && cellContent.Last().Type == ElementType.Paragraph
              && !cellContent.Last().Nodes().Any()
              && !(lastBreak is ParagraphBreak))
            {
              cellContent.Last().Remove();
              cellContent = cellContent.Take(cellContent.Length - 1).ToArray();
            }

            // Convert the paragraph to a cell where appropriate
            if (cellContent.Length == 1 && cellContent[0].Type == ElementType.Paragraph)
            {
              cellContent[0].Type = ElementType.TableCell;
            }
            else
            {
              foreach (var content in cellContent)
                content.Remove();
              var cell = new Element(ElementType.TableCell, cellContent);
              cell.SetStyles(groups.Peek().NormalizedStyles(document));
              parent.Add(cell);
            }
            
            var nextParagraph = new Element(ElementType.Paragraph);
            parent.Add(nextParagraph);
            paragraph = nextParagraph;
          }
          else if (token is ListId)
          {
            paragraph.Type = ElementType.ListItem;
          }
          else if (token is RowBreak || token is NestedRowBreak)
          {
            lastParagraphStyle = null;
            var parent = paragraph.Parent;
            if (paragraph.Type == ElementType.TableCell)
            {
              paragraph.SetStyles(groups.Peek().NormalizedStyles(document));
            }
            else
            {
              paragraph.Remove();
              paragraph = parent.Elements().Last();
            }
            var currLevel = paragraph.TableLevel;
            var cells = parent.Elements().Reverse()
              .TakeWhile(e => e.Type == ElementType.TableCell && e.TableLevel == currLevel).Reverse()
              .ToList();
            if (cells.Count > 0)
            {
              var row = new Element(ElementType.TableRow);
              var rowStyles = new StyleList(groups.Peek().Styles);
              rowStyles.Merge(new NestingLevel(Math.Max(currLevel - 1, 0)));
              rowStyles.RemoveWhere(t => t is HalfCellPadding || t is BorderToken);
              var cellFormats = rowStyles.OfType<CellToken>().ToList();
              if (cellFormats.Count < cells.Count)
                throw new InvalidOperationException("Fewer cell styles were found than cell breaks");

              for (var i = 0; i < cells.Count; i++)
              {
                cells[i].Remove();
                cells[i].Styles.RemoveWhere(t => t is CellToken);
                cells[i].Styles.Add(cellFormats[i]);
                if (i > 0 && cellFormats[i].Styles.OfType<CellMergePrevious>().Any())
                  cellFormats[i - 1].ColSpan++;
                else
                  row.Add(cells[i]);
              }

              if (rowStyles.TryRemoveMany(t => t is TablePaddingBottom || t is TablePaddingLeft
                || t is TablePaddingRight || t is TablePaddingTop, out var paddings))
              {
                foreach (var cell in cells)
                  cell.Styles.MergeRange(paddings);
              }
              row.SetStyles(rowStyles);
              parent.Add(row);
            }
            var nextParagraph = new Element(ElementType.Paragraph);
            parent.Add(nextParagraph);
            paragraph = nextParagraph;
          }
          else if (token is ObjectAttachment)
          {
            paragraph.Add(new Anchor(AnchorType.Attachment, attachmentIndex.ToString()));
            attachmentIndex++;
          }
          else if (token is OutlineLevel outlineLevel && outlineLevel.Value >= 0 && outlineLevel.Value < 6)
          {
            switch (outlineLevel.Value + 1)
            {
              case 2:
                paragraph.Type = ElementType.Header2;
                break;
              case 3:
                paragraph.Type = ElementType.Header3;
                break;
              case 4:
                paragraph.Type = ElementType.Header4;
                break;
              case 5:
                paragraph.Type = ElementType.Header5;
                break;
              case 6:
                paragraph.Type = ElementType.Header6;
                break;
              default:
                paragraph.Type = ElementType.Header1;
                break;
            }
          }
          else if (token.Type == TokenType.CellFormat
            || token.Type == TokenType.CharacterFormat
            || token.Type == TokenType.ParagraphFormat
            || token.Type == TokenType.RowFormat
            || token.Type == TokenType.SectionFormat)
          {
            if ((token is ParagraphDefault || token is SectionDefault) && paragraph.Nodes().Any())
              paragraph.SetStyles(groups.Peek().NormalizedStyles(document));
            //stylesBeforeReset = groups.Peek().Styles.ToList();
            else if (token is ListLevelNumber listLevelNumber)
              paragraph.Type = ElementType.ListItem;
            groups.Peek().AddStyle(token);
          }

          lastBreak = token.Type == TokenType.BreakTag ? token : lastBreak;
        }

        var state = groups.Pop();
        lastParagraphStyle = lastParagraphStyle ?? state;
      }

      if (paragraph.Nodes().Any())
      {
        if (!paragraph.Styles.Any())
          paragraph.SetStyles(lastParagraphStyle.NormalizedStyles(document));
      }
      else
      {
        paragraph.Remove();
      }

      AddFootnotes(root, footnotes, document, defaultStyles);

      OrganizeMargins(root);
      OrganizeTable(root);
      OrganizeLists(root);
      OrganizeParagraphBorders(root);
      return root;
    }

    private void AddFootnotes(Element root, List<IToken> footnoteGroup, Document document, List<IToken> defaultStyles)
    {
      if (footnoteGroup.Count < 1)
        return;
      var section = root.Elements().Last();
      section.Add(new HorizontalRule());
      var footnoteParas = Build(footnoteGroup, document, defaultStyles).Elements().First().Elements().ToList();
      foreach (var paragraph in footnoteParas)
      {
        paragraph.Remove();
        section.Add(paragraph);
      }
      footnoteGroup.Clear();
    }

    private void ProcessFootnoteGroups(List<IToken> footnote, List<IToken> clone, int footnoteIndex)
    {
      foreach (var token in footnote)
      {
        if (token is Group group)
        {
          var cloneGroup = new Group();
          ProcessFootnoteGroups(group.Contents, cloneGroup.Contents, footnoteIndex);
          clone.Add(cloneGroup);
        }
        else if (token is FootnoteReference)
        {
          var footnoteId = "footnote" + footnoteIndex.ToString("D2");
          clone.Add(new Group()
          {
            Contents =
            {
              new IgnoreUnrecognized(),
              new BookmarkStart(),
              new TextToken() { Value = footnoteId }
            }
          });
          clone.Add(new TextToken() { Value = footnoteIndex.ToString() + " " });
          clone.Add(new Group()
          {
            Contents =
            {
              new IgnoreUnrecognized(),
              new BookmarkEnd(),
              new TextToken() { Value = footnoteId }
            }
          });
        }
        else if (!(token is Footnote))
        {
          clone.Add(token);
        }
      }
    }
      
    private void OrganizeTable(Element root)
    {
      var parents = root.Descendants()
        .Where(e => e.Type != ElementType.Table
          && e.Elements().Any(c => c.Type == ElementType.TableRow))
        .ToList();
      var tables = new List<Element>();
      
      foreach (var parent in parents)
      {
        var nodeList = new List<Node>();
        foreach (var node in parent.Nodes().ToList())
        {
          node.Remove();
          if (node is Element row && row.Type == ElementType.TableRow)
          {
            if (!(nodeList.LastOrDefault() is Element table 
              && table.Type == ElementType.Table))
            {
              table = new Element(ElementType.Table);
              tables.Add(table);
              nodeList.Add(table);
            }
            
            table.Add(row);
            row.Styles.RemoveWhere(t => t is LeftIndent || t is RightIndent || t is SpaceAfter || t is SpaceBefore);
            if (!table.Styles.Any())
              table.SetStyles(row.Styles.Where(t => t.Type != TokenType.CellFormat 
                && t.Type != TokenType.RowFormat
                && !(t is TextAlign || t is LeftIndent || t is RightIndent)));
          }
          else
          {
            nodeList.Add(node);
          }
        }

        foreach (var node in nodeList)
          parent.Add(node);
      }

      foreach (var table in tables)
      {
        if (table.Elements().All(r => r.Styles.OfType<RowLeft>().Any()))
          table.Styles.Merge(table.Elements().First().Styles.OfType<RowLeft>().First());
        var headerRows = table.Elements().TakeWhile(r => r.Styles.OfType<HeaderRow>().Any()).ToList();
        if (headerRows.Count > 0)
        {
          var head = new Element(ElementType.TableHeader);
          head.SetStyles(headerRows[0].Styles);
          foreach (var row in headerRows)
          {
            row.Remove();
            head.Add(row);
            foreach (var cell in row.Elements().Where(e => e.Type == ElementType.TableCell))
              cell.Type = ElementType.TableHeaderCell;
          }
          var body = new Element(ElementType.TableBody);
          foreach (var row in table.Elements().ToList())
          {
            row.Remove();
            body.Add(row);
          }
          if (body.Elements().Any())
            body.SetStyles(body.Elements().First().Styles);
          table.Add(head);
          table.Add(body);
        }
      }
    }

    private void OrganizeParagraphBorders(Element root)
    {
      foreach (var section in root.Elements()
        .Where(s => s.Elements()
          .Where(e => e.Styles.OfType<BorderToken>().Any() && e.Type != ElementType.Table)
          .Skip(1).Any()
        ))
      {
        var nodeList = new List<Node>();
        foreach (var node in section.Nodes().ToList())
        {
          node.Remove();
          if (node is Element paragraph)
          {
            var last = nodeList.Count > 0 ? nodeList[nodeList.Count - 1] as Element : null;
            var previous = last?.Type == ElementType.Container ? last.Elements().Last() : last;
            if (previous != null && SameBorders(previous, paragraph))
            {
              if (last.Type == ElementType.Container)
              {
                last.Add(paragraph);
              }
              else
              {
                var container = new Element(ElementType.Container, previous, paragraph);
                container.Styles.AddRange(previous.Styles);
                nodeList[nodeList.Count - 1] = container;
              }
            }
            else
            {
              nodeList.Add(paragraph);
            }
          }
          else
          {
            nodeList.Add(node);
          }
        }

        foreach (var node in nodeList)
          section.Add(node);
      }
    }

    private void OrganizeMargins(Element root)
    {
      foreach (var section in root.Elements()
        .Where(s => s.Elements()
          .Where(e => e.Styles.OfType<ContextualSpace>().Any())
          .Skip(1).Any()
        ))
      {
        var previous = section.Elements().First();
        foreach (var paragraph in section.Elements().Skip(1))
        {
          if ((previous.Styles.OfType<StyleRef>().FirstOrDefault()?.Value ?? -1)
            == (paragraph.Styles.OfType<StyleRef>().FirstOrDefault()?.Value ?? -2)
            && (previous.Styles.OfType<ContextualSpace>().Any() || paragraph.Styles.OfType<ContextualSpace>().Any()))
          {
            previous.Styles.RemoveWhere(t => t is SpaceAfter);
            paragraph.Styles.RemoveWhere(t => t is SpaceBefore);
          }
          previous = paragraph;
        }
      }
    }

    private bool SameBorders(Element x, Element y)
    {
      if (x.Type != y.Type || x.Type == ElementType.Table || y.Type == ElementType.Table
        || x.Styles.OfType<ParagraphBorderBetween>().Any()
        || y.Styles.OfType<ParagraphBorderBetween>().Any())
        return false;

      var xBorders = x.Styles.OfType<BorderToken>().OrderBy(b => b.Side).ToList();
      var yBorders = y.Styles.OfType<BorderToken>().OrderBy(b => b.Side).ToList();
      if (xBorders.Count < 1 || yBorders.Count < 1 || xBorders.Count != yBorders.Count)
        return false;
      for (var i = 0; i < xBorders.Count; i++)
      {
        if (!xBorders[i].Equals(yBorders[i]))
          return false;
      }
      return true;
    }

    private void OrganizeLists(Element root)
    {
      var listIndices = new Dictionary<int, List<int>>();

      foreach (var listItem in root.Descendants()
        .Where(e => e.Type == ElementType.ListItem))
      {
        var listId = listItem.Styles.OfType<ListStyleId>().FirstOrDefault()?.Value
          ?? (int?)listItem.Styles.OfType<NumberingTypeToken>().FirstOrDefault()?.Value
          ?? 1;
        if (!listIndices.TryGetValue(listId, out var currentLevels))
        {
          currentLevels = new List<int>();
          listIndices[listId] = currentLevels;
        }

        while (currentLevels.Count <= listItem.ListLevel)
          currentLevels.Add(listItem.Styles.OfType<NumberingStart>().FirstOrDefault()?.Value - 1 ?? 0);
        while (currentLevels.Count > listItem.ListLevel + 1)
          currentLevels.RemoveAt(currentLevels.Count - 1);
        currentLevels[listItem.ListLevel]++;

        listItem.Styles.Merge(new NumberingStart(currentLevels[listItem.ListLevel]));

        if (listItem.Parent.Type == ElementType.TableCell || listItem.Parent.Type == ElementType.TableHeaderCell)
        {
          listItem.Parent.Styles.RemoveWhere(t => t is LeftIndent || t is FirstLineIndent);
          var leftIndent = listItem.Styles.OfType<LeftIndent>().FirstOrDefault();
          var firstLineIndent = listItem.Styles.OfType<FirstLineIndent>().FirstOrDefault();
          if (leftIndent != null && firstLineIndent != null 
            && (leftIndent.Value + firstLineIndent.Value).ToPx() < 0)
          {
            listItem.Styles.Merge(new FirstLineIndent(new UnitValue(leftIndent.Value.ToPx() * -1, UnitType.Pixel)));
          }
        }
      }

      var parents = root.Descendants()
        .Where(e => e.Type != ElementType.List && e.Type != ElementType.OrderedList 
          && e.Elements().Any(c => c.Type == ElementType.ListItem))
        .ToList();

      foreach (var parent in parents)
      {
        var nodeList = new List<Node>();
        foreach (var node in parent.Nodes().ToList())
        {
          node.Remove();
          if (node is Element listItem && listItem.Type == ElementType.ListItem)
          {
            var list = nodeList.LastOrDefault() as Element;
            if (list != null)
            {
              if (list.Type == ElementType.List || list.Type == ElementType.OrderedList)
              {
                var currListId = listItem.Styles.OfType<ListStyleId>().FirstOrDefault()?.Value 
                  ?? (int?)listItem.Styles.OfType<NumberingTypeToken>().FirstOrDefault()?.Value
                  ?? 1;
                var prevListId = list.Styles.OfType<ListStyleId>().FirstOrDefault()?.Value
                  ?? (int?)list.Styles.OfType<NumberingTypeToken>().FirstOrDefault()?.Value
                  ?? 1;
                if (currListId != prevListId)
                  list = null;
              }
              else
              {
                list = null;
              }
            }

            if (list == null)
            {
              list = new Element(ElementType.List);
              nodeList.Add(list);
            }

            for (var i = 0; i < listItem.ListLevel; i++)
            {
              var lastItem = list.Elements().LastOrDefault();
              if (lastItem != null)
              {
                var lastElement = lastItem.Nodes().LastOrDefault() as Element;
                if (lastElement?.Type != ElementType.List
                  && lastElement?.Type != ElementType.OrderedList)
                {
                  lastElement = new Element(ElementType.List);
                  lastItem.Add(lastElement);
                }
                list = lastElement;
              }
            }

            if (!list.Elements().Any())
            {
              var numType = listItem.Styles.OfType<ListLevelType>().FirstOrDefault()?.Value
                ?? (listItem.Styles.OfType<NumberingLevelBullet>().Any() ? (NumberingType?)NumberingType.Bullet : null)
                ?? listItem.Styles.OfType<NumberingTypeToken>().FirstOrDefault()?.Value
                ?? NumberingType.Bullet;

              if (numType != NumberingType.Bullet && numType != NumberingType.NoNumber)
                list.Type = ElementType.OrderedList;
            }

            list.Add(listItem);
            if (!list.Styles.Any())
            {
              list.SetStyles(listItem.Styles);

              var parentIndexPx = list.Parents()
                .Where(e => e.Type == ElementType.List || e.Type == ElementType.OrderedList)
                .SelectMany(e => e.Styles.OfType<LeftIndent>())
                .Select(i => i.Value.ToPx())
                .Sum();
              if (parentIndexPx > 0)
              {
                var newIndent = (listItem.Styles.OfType<LeftIndent>().FirstOrDefault()?.Value.ToPx() ?? 0)
                  - parentIndexPx;
                list.Styles.Merge(new LeftIndent(new UnitValue(newIndent, UnitType.Pixel)));
              }
            }
            listItem.Styles.RemoveWhere(t => t is LeftIndent || t is RightIndent || t is FirstLineIndent);
          }
          else
          {
            nodeList.Add(node);
          }
        }

        foreach (var node in nodeList)
          parent.Add(node);
      }
    }
    
    private class TokenState
    {
      private readonly StyleList _styles = new StyleList();
      private readonly List<IToken> _defaultStyles;

      public IEnumerator<IToken> Tokens { get; }
      public IEnumerable<IToken> Styles => _styles;

      public TokenState(IEnumerable<IToken> tokens, List<IToken> defaultStyles)
      {
        Tokens = tokens.GetEnumerator();
        _defaultStyles = defaultStyles;
        _styles.AddRange(defaultStyles);
      }

      public TokenState(IEnumerable<IToken> tokens, TokenState previous)
      {
        Tokens = tokens.GetEnumerator();
        _styles.AddRange(previous.Styles);
        _defaultStyles = previous._defaultStyles;
      }

      public IEnumerable<IToken> NormalizedStyles(Document document)
      {
        var styleId = Styles.OfType<ListStyleId>().FirstOrDefault();
        if (styleId == null || !document.ListStyles.TryGetValue(styleId.Value, out var listStyle))
          return Styles
            .Where(t => !HtmlVisitor.IsSpanElement(t));

        var levelNum = Styles.OfType<ListLevelNumber>().FirstOrDefault() ?? new ListLevelNumber(0);
        var result = new StyleList(listStyle.Style.Levels[levelNum.Value]
          .Where(t => t.Type == TokenType.ParagraphFormat));
        result.MergeRange(Styles
          .Where(t => !HtmlVisitor.IsSpanElement(t)));
        return result;
      }

      public void AddStyle(IToken token)
      {
        _styles.Merge(token);
        if (token is PlainStyle && _defaultStyles?.Count > 0)
          _styles.MergeRange(_defaultStyles);
      }
    }
  }
}
