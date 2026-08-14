create table hm_inisettings
(
	inisettingid int identity (1, 1) not null,
	inisettingname nvarchar(100) not null,
	inisettingvalue ntext not null,
	inisettingfilevalue ntext not null
)

ALTER TABLE hm_inisettings ADD CONSTRAINT hm_inisettings_pk PRIMARY KEY NONCLUSTERED (inisettingid)

ALTER TABLE hm_inisettings ADD CONSTRAINT u_inisettingname UNIQUE (inisettingname)

update hm_dbversion set value = 6011
